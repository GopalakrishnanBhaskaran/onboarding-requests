using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <summary>
/// v2.1 agent provisioning (worker only; <see cref="WorkflowSettings.AgentProvisioning"/> /
/// <see cref="WorkflowSettings.AgentRoleAssignment"/>). Runs only at With ID Team / With Role Team, which are reached only
/// after the named Owner and then Security approved — and re-checks both approvals before any write. Deterministic code,
/// never the AI model. Reads before every write, so a repeat finds the object instead of creating it again. The Temporary
/// Access Pass stays with the ID team unless the agent TAP is on (<see cref="WorkflowSettings.AgentTap"/>): then, for a user
/// it has just created, the agent mails one one-time pass with the initial password and the MFA steps to the joiner's
/// Enterprise ID approved on the request (Gokul, 2 Nov 2026), else the people inventory's address checked against Entra
/// (F2; <see cref="TapRecipient"/>) — never stored, never redirected. Anything outside the safe path goes to the team with the reason and an issue.
/// </summary>
public sealed partial class WorkflowAgent
{
    public const string AgentName = "AI agent";

    /// <summary>
    /// Sign-in details already mailed in this process, by request id: if saving the request then fails (someone acted
    /// meanwhile), the repeat reuses this instead of issuing a second pass.
    /// </summary>
    private readonly ConcurrentDictionary<string, AgentWork> signInDetailsSent = new();

    /// <summary>
    /// The agent mails the pass itself: switched on, the TAP mail sender configured (Graph, <c>TAP_MAIL_SENDER</c>) and a
    /// member account. Where it goes is decided only by <see cref="TapRecipient"/>: the approved Enterprise ID, never another body field.
    /// </summary>
    private bool TapByAgent(AccessRequest r) => settings.AgentTap && directMail?.Delivers == true && !r.Guest;

    /// <summary>The people inventory entry for the account the agent creates (F2: checks and backs up the TAP recipient).</summary>
    private async Task<Person?> PersonAsync(AccessRequest r, CancellationToken ct) =>
        (await stores.PeopleInventory.GetAsync(r.ProjectId, Person.MakeId(r.DomainId!), ct))?.Value;

    /// <summary>
    /// F2: the recipient could not be verified, so no pass is mailed. One escalation per request and account (a repeat
    /// finds it), audited; the ID team hands the pass over (the task says why).
    /// </summary>
    private async Task<string> TapMismatchAsync(AccessRequest r, string reason, string cid, CancellationToken ct)
    {
        var upn = r.DomainId!.ToLowerInvariant();
        var e = new Escalation
        {
            Id = Escalation.MakeId(EscalationKind.TapRecipientMismatch, $"{r.ProjectId}|{r.Id}|{upn}"), ProjectId = r.ProjectId,
            Kind = EscalationKind.TapRecipientMismatch, RequestId = r.Id, RequestRevision = r.Revision, User = upn,
            Group = r.Flow?.Scope?.EntraGroup, Environment = r.Flow?.Scope?.Environment ?? r.Environment,
            Reason = $"TAP recipient mismatch: {reason}. Nothing was mailed; the ID team hands the pass over.",
            RequestedBy = AgentName, CreatedAt = clock.UtcNow, CorrelationId = cid,
        };
        try
        {
            await stores.Escalations.CreateAsync(e, ct);
            await stores.AuditEvents.CreateAsync(new AuditEvent
            {
                Id = AuditEvent.NewId(clock.UtcNow), ProjectId = r.ProjectId, At = clock.UtcNow, Actor = Agent, Action = "escalation-raised",
                Target = $"request {r.Id}", CorrelationId = cid, Details = $"TapRecipientMismatch {e.Id}: {reason}",
            }, ct);
        }
        catch (DocumentConflictException)
        {
            // Raised already for this request and account.
        }
        return $"TAP recipient mismatch: {reason} (escalation {e.Id})";
    }

    private bool ProvisioningPending(TeamTask task, ReviewedScope? scope) =>
        settings.AgentProvisioning && task.State == TeamTaskState.Open && (task.Agent is not ({ Completed: true } or { Manual: not null }) || ResumeReason(task, scope) is not null);

    private bool RoleAssignmentPending(TeamTask task, ReviewedScope? scope) =>
        settings.AgentRoleAssignment && task.State == TeamTaskState.Open && (task.Agent is not ({ Completed: true } or { Manual: not null }) || ResumeReason(task, scope) is not null);

    private const string PrivilegedLeft = " is privileged access (agent privileged provisioning is off)";
    private const string NotAllowListed = "are not on the agent's allow-list (role or scope)";

    /// <summary>
    /// Why the agent takes back a task it left to a team, or null: the setting that made it leave now allows it —
    /// privileged rows switched on (also the older wording), or every approved Azure role now on its allow-list — and
    /// nobody in the team has picked the task up. The agent then runs every check again.
    /// </summary>
    private string? ResumeReason(TeamTask task, ReviewedScope? scope)
    {
        if (task.PickedUpAt is not null || task.Agent is not { Completed: false, Manual: { } m }) return null;
        if (settings.AgentPrivileged && (m.EndsWith(PrivilegedLeft, StringComparison.Ordinal) || m.EndsWith(" is privileged access", StringComparison.Ordinal)))
            return "privileged rows are now done by the AI agent";
        if (task.Team == AppRole.RoleTeam && databricks is not null && m == DatabricksOff)
            return "the agent's Databricks set-up is now on";
        if (task.Team == AppRole.RoleTeam && m.Contains(NotAllowListed, StringComparison.Ordinal) && scope?.Grants() is { Count: > 0 } grants
            && grants.All(g => g.RoleId is not null && g.Scope is not null && AllowListed(g)))
            return "every approved Azure role is now on the agent's allow-list";
        return null;
    }

    private bool AllowListed(AzureRoleGrant g) =>
        settings.RoleAssignAllowedRoleIds.Contains(g.RoleId!, StringComparer.OrdinalIgnoreCase) && settings.RoleAssignScopes.Any(a => InScope(g.Scope!, a));

    /// <summary>Why a privileged row stays with the people, or null when the agent may do it.</summary>
    private string? PrivilegedReason(MappingRow? row, ReviewedScope scope)
    {
        var never = scope.Grants().Where(BuiltInRoles.IsNeverByAgent).Select(g => g.RoleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (never.Count > 0)
            return $"mapping row {scope.MappingRowId} includes {string.Join(", ", never)}: privileged administrator roles are never handled by the agent";
        return row?.Privileged == true && !settings.AgentPrivileged ? $"mapping row {scope.MappingRowId}{PrivilegedLeft}" : null;
    }

    /// <summary>A task taken back from the team (see <see cref="ResumeReason"/>): the trail says so, the agent is waited on.</summary>
    private AccessRequest Resumed(AccessRequest r, TeamTask task, DateTimeOffset now) => ResumeReason(task, r.Flow!.Scope) is not { } why ? r : r with
    {
        Flow = r.Flow!.Waiting(WaitingOn.Agent, now).Log(now, Agent, "agent-resumed",
            $"{char.ToUpperInvariant(why[0])}{why[1..]}; the AI agent takes the {(task.Team == AppRole.IdTeam ? "ID team" : "role assignment team")} task back (nobody had picked it up) and runs every check again."),
    };

    private string PrivilegedNote(MappingRow? row) =>
        row?.Privileged == true ? " Privileged access: done by the agent because privileged rows are switched on (after both approvals)." : "";

    private bool BothApprovalsCurrent(WorkflowState flow) =>
        flow.CurrentApproval(ApprovalStage.Owner)?.Decision == ApprovalDecision.Approved
        && flow.CurrentApproval(ApprovalStage.Security)?.Decision == ApprovalDecision.Approved;

    // ---- Stage 4: user, group, membership --------------------------------------------------------------------------

    private async Task<Step?> ProvisionAsync(AccessRequest r, TeamTask task, string cid, CancellationToken ct)
    {
        var now = clock.UtcNow;
        r = Resumed(r, task, now);
        var flow = r.Flow!;
        var scope = flow.Scope!;
        var project = await ProjectAsync(r, ct);
        var sheet = await sources.ReadMappingSheetAsync(r.ProjectId, ct);
        var row = sheet.FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var work = (task.Agent ?? new AgentWork { At = now }) with { Manual = null };
        if (work.SignInDetailsSentAt is null && signInDetailsSent.TryGetValue(r.Id, out var mailed)) work = mailed; // the save after the mail was lost

        if (!BothApprovalsCurrent(flow))
            return ToIdTeam(r, task, work with { Manual = "the Owner and Security approvals are not both current" }, project, now, IssueSeverity.High);
        var manual = Tenants.NotConnected(project, settings.AgentTenantId)
            ?? (r.Guest ? "guest accounts are invited by the ID team, not created by the agent"
            : PrivilegedReason(row, scope)
            ?? (row?.Flag == RowFlag.Red ? $"mapping row {scope.MappingRowId} is flagged red (needs a human decision)" : null));
        if (manual is not null) return ToIdTeam(r, task, work with { Manual = manual }, project, now, IssueSeverity.Info);

        Secret? password = null;
        try
        {
            var user = await directory!.FindUserAsync(r.DomainId!, ct);
            var domain = project?.DomainIdSuffix?.Trim();
            if (user is null && !string.IsNullOrEmpty(domain) && !r.DomainId!.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
            {
                // E.g. submitted before the project's domain was set: the agent creates accounts only in the project's domain.
                return ToIdTeam(r, task, work with { Manual = $"{r.DomainId} is not in the project's domain {domain}, so the agent does not create it (the requester can resubmit)" },
                    project, now, IssueSeverity.Info);
            }
            if (user?.IsGuest == true)
                return ToIdTeam(r, task, work with { Manual = $"{r.DomainId} is a guest account" }, project, now, IssueSeverity.Info);
            // Before an account is created for the agent TAP, there must be a mailbox for its pass (the approved Enterprise
            // ID, else the people inventory); otherwise the account is still created and the ID team hands the pass over.
            if (user is null && work.TapLeft is null && TapByAgent(r) && TapRecipient.Before(await PersonAsync(r, ct), r.DomainId!.ToLowerInvariant(), r) is { Ok: false } pre)
                work = work with { TapLeft = await TapMismatchAsync(r, pre.Reason!, cid, ct) };
            // The password and the pass go out in one mail, so the account is created only once the TAP permission is in
            // the agent's token (a new permission can take ~24 h, F61): until then this waits like any refused write;
            // after that the agent provisions anyway and the ID team issues the pass.
            if (user is null && work.TapLeft is null && TapByAgent(r) && !await writer!.CanIssueTapAsync(ct))
            {
                if (work.RefusedSince is not { } since || now - since < settings.PermissionWait) throw new DirectoryWriteException("TapForbidden");
                work = work with { TapLeft = $"the TAP permission had not reached the agent {settings.PermissionWait.TotalHours:0} h after the first try" };
            }
            // The pass goes out through Graph from the TAP sender mailbox (F2), not through the ACS queue: the ACS mail limit
            // (F70) does not hold it back. An Exchange refusal leaves the pass to the ID team (below).

            if (work.GroupObjectId is null)
            {
                // A group the agent created is used by id afterwards: a name filter can lag right after creation.
                var groups = await directory.FindGroupsAsync(scope.EntraGroup, ct);
                if (groups.Count > 1)
                    return ToIdTeam(r, task, work with { Manual = $"{groups.Count} groups are named {scope.EntraGroup} in Entra" }, project, now, IssueSeverity.High);
                if (groups.Count == 1 && !groups[0].SecurityEnabled)
                    return ToIdTeam(r, task, work with { Manual = $"{scope.EntraGroup} is not a security group" }, project, now, IssueSeverity.High);
                if (groups.Count == 1 && row?.GroupObjectId is { } expectedId && !string.Equals(expectedId.Trim(), groups[0].ObjectId, StringComparison.OrdinalIgnoreCase))
                    return ToIdTeam(r, task, work with { Manual = $"{scope.EntraGroup} in Entra has object id {groups[0].ObjectId}, the mapping sheet says {expectedId}" },
                        project, now, IssueSeverity.High);
                if (groups.Count == 1)
                {
                    work = work with { GroupObjectId = groups[0].ObjectId };
                }
                else if (scope.GroupState == GroupState.ToBeCreated)
                {
                    var created = await writer!.CreateSecurityGroupAsync(scope.EntraGroup,
                        $"PMO onboarding mapping row {scope.MappingRowId}. Created by the AI agent after Owner and Security approval.", ct);
                    work = work with { GroupObjectId = created.ObjectId, GroupCreated = true };
                }
                else
                {
                    return ToIdTeam(r, task, work with { Manual = $"the mapping sheet says {scope.EntraGroup} exists, but it is not in Entra" }, project, now, IssueSeverity.High);
                }
            }

            // The account last: the group is in place, so its password is mailed in this same run (it is never kept).
            if (user is null)
            {
                password = Secret.NewPassword();
                user = await writer!.CreateUserAsync(new NewUser(r.DomainId!.ToLowerInvariant(), $"{r.FirstName} {r.LastName}".Trim(),
                    MailNickname(r), r.FirstName, r.LastName), password, ct);
                work = work with { UserCreated = true };
            }
            work = work with { UserObjectId = user.ObjectId };

            // Agent TAP: right after the account exists (its password is only in memory now), its sign-in details go out
            // by mail, once. A transient failure is retried next run (then without the password); any other leaves the
            // pass to the ID team.
            if (work.UserCreated && work.SignInDetailsSentAt is null && work.TapLeft is null && TapByAgent(r))
                work = await SendSignInDetailsAsync(r, task, scope, project, work, user, password, cid, ct);

            // A user created seconds ago is in no group yet (and may not be readable yet, F58): add without the read.
            if (password is not null || !await directory.IsMemberAsync(work.GroupObjectId!, user.ObjectId, ct))
            {
                await writer!.AddMemberAsync(work.GroupObjectId!, user.ObjectId, ct);
                work = work with { MemberAdded = true };
            }
            work = work with { Membership = true };
        }
        catch (DirectoryWriteException e) when (e.Forbidden)
        {
            return RefusedOrTeam(r, task, work, e.Category, project, now, isIdTeam: true);
        }
        catch (DirectoryReadException e)
        {
            return RetryOrTeam(r, task, work, $"provision:{task.Id}", e.Category, project, now, isIdTeam: true);
        }

        // The group now exists: mark the row (PMO's sheet records facts, the agent only flips 'to be created').
        var newScope = scope;
        if (scope.GroupState == GroupState.ToBeCreated && row is { GroupState: GroupState.ToBeCreated } && MappingSheet.Same(row.EntraGroup, scope.EntraGroup))
        {
            var stored = await stores.MappingSheet.GetAsync(r.ProjectId, row.Id, ct);
            if (stored is not null && stored.Value.Version == row.Version)
            {
                var updated = stored.Value with { GroupState = GroupState.Exists, Version = row.Version + 1, UpdatedBy = Agent, UpdatedAt = now };
                try
                {
                    await stores.MappingSheet.ReplaceAsync(updated, stored.ETag, ct);
                    work = work with { MappingUpdated = true };
                    // Only the agent's own change: keep the reviewed fingerprint in step so no re-review is triggered.
                    if (MappingSheet.Fingerprint(sheet, r.Resource) == scope.SheetFingerprint)
                        newScope = scope with { SheetFingerprint = MappingSheet.Fingerprint(sheet.Select(x => x.RowId == row.RowId ? updated : x), r.Resource) };
                }
                catch (ConcurrencyException)
                {
                    // PMO changed the row meanwhile: the mapping-change check re-reviews the request.
                }
            }
        }

        work = work with { At = now, Completed = true };
        var done = Done(work, r, scope) + PrivilegedNote(row);
        List<Func<CancellationToken, Task>> resolved = work.RefusedSince is null ? []
            : [c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.DependencyFailure], c)]; // the permission arrived
        // Security oversees the agent's writes; the requester follows their request. The Owner is mailed only to decide.
        var informed = Recipients.Requester(r).Concat(Recipients.Security(project));
        if (work.UserCreated && work.SignInDetailsSentAt is null)
        {
            // A new account needs a one-time TAP for the first sign-in: the ID team issues it.
            var why = work.TapLeft is null ? "" : $" The agent did not mail the sign-in details: {work.TapLeft}.";
            var tapTask = task with { Agent = work, Instructions = Messages.IdTeamTapInstructions(r, newScope, flow, done + why, settings.TapMaxLifetimeMinutes), SinceAt = now };
            var next = (flow with { Scope = newScope, IdTeamTask = tapTask }).Waiting(WaitingOn.IdTeam, now)
                .Log(now, Agent, "agent-provisioned", $"{done} The ID team issues the one-time Temporary Access Pass.{why}");
            var (subject, body) = Messages.TeamTask(r, tapTask, settings);
            var (ps, pb) = Messages.Provisioned(r, newScope, done, $"The ID team issues the one-time Temporary Access Pass next.{why}", settings);
            List<Func<CancellationToken, Task>> effects =
            [
                .. resolved,
                c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{tapTask.Id}:tap", NotificationKind.TeamTask, Recipients.IdTeam(project), subject, body, c),
                c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{task.Id}:provisioned", NotificationKind.ProvisioningSummary, informed, ps, pb, c),
            ];
            if (work.TapLeft is not null)
                effects.Add(c => issues.RecordAsync(r.ProjectId, r.Id, $"{task.Id}:tap", IssueKind.NeedsDeveloper, IssueSeverity.Warning,
                    $"Agent TAP left to the ID team: {work.TapLeft}.", null, c));
            return new Step(r with { Flow = next }, effects, Stop: true);
        }

        // Existing account (no TAP needed), or a new one whose sign-in details the agent mailed: nothing left for the ID
        // team. Verified on the next run (Entra reads lag right after a write).
        var byMail = work.SignInDetailsSentAt is not null;
        var report = new IdTeamReport
        {
            UserPrincipalName = r.DomainId!.ToLowerInvariant(), UserObjectId = work.UserObjectId, GroupName = scope.EntraGroup,
            GroupObjectId = work.GroupObjectId, GroupCreated = work.GroupCreated, UserCreated = work.UserCreated, AddedToGroup = true,
            MappingSheetUpdated = work.MappingUpdated,
            Tap = byMail ? new TapRecord(work.TapIssuedAt!.Value, work.TapExpiresAt!.Value, true, AgentName, now) : null,
            Notes = byMail
                ? $"Done by the AI agent after Owner and Security approval; one-time TAP and {(work.PasswordMailed ? "initial password" : "MFA steps (no password)")} mailed to {work.SignInDetailsSentTo} ({work.SignInDetailsFrom ?? TapRecipient.FromInventorySource}; never stored), with the instruction to change the password right after signing in with the pass."
                : "Done by the AI agent after Owner and Security approval; existing account, no TAP needed.",
        };
        var reported = task with
        {
            Agent = work, State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = AgentName, IdReport = report,
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? AgentName,
        };
        var mailedNote = byMail
            ? $"The agent mailed the sign-in details (one-time Temporary Access Pass valid until {Messages.Time(work.TapExpiresAt)}" +
              $"{(work.PasswordMailed ? ", initial password" : "")}, MFA set-up steps, change the password right after signing in) to {work.SignInDetailsSentTo} ({work.SignInDetailsFrom ?? TapRecipient.FromInventorySource}). Nobody else saw them; the app keeps no copy."
            : "Existing account: no Temporary Access Pass needed.";
        var verify = (flow with { Scope = newScope, IdTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, Agent, "agent-provisioned", $"{done} {mailedNote} Verified on the next agent run.");
        var (s1, b1) = Messages.Provisioned(r, newScope, done, $"{mailedNote} The agent verifies it, then the roles are assigned.", settings);
        return new Step(r with { Flow = verify },
        [
            .. resolved,
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{task.Id}:provisioned", NotificationKind.ProvisioningSummary,
                byMail ? informed.Concat(Recipients.IdTeam(project)) : informed, s1, b1, c),
        ], Stop: true);
    }

    /// <summary>
    /// Agent TAP: checks the recipient (F2), then creates one one-time pass for the user the agent created and mails it —
    /// with the initial password when it was created in this run — and the MFA steps to that one address. The values exist
    /// only in memory and in that mail; the app stores no copy, only an audit event (user id, masked recipient, time, expiry,
    /// correlation id). A transient Graph failure is thrown (the run is retried); any other failure leaves the pass to the
    /// ID team.
    /// </summary>
    private async Task<AgentWork> SendSignInDetailsAsync(AccessRequest r, TeamTask task, ReviewedScope scope, Project? project, AgentWork work, DirectoryUser user,
        Secret? password, string cid, CancellationToken ct)
    {
        if (!BothApprovalsCurrent(r.Flow!)) return work with { TapLeft = "the Owner and Security approvals are not both current" };
        var upn = r.DomainId!.ToLowerInvariant();
        // Checked before the pass exists: a pass nobody may receive is never created.
        var check = TapRecipient.After(await PersonAsync(r, ct), upn, user, r);
        if (!check.Ok) return work with { TapLeft = await TapMismatchAsync(r, check.Reason!, cid, ct) };
        var to = check.To!;
        IssuedTap tap;
        try
        {
            tap = await writer!.CreateTemporaryAccessPassAsync(work.UserObjectId!, settings.AgentTapLifetimeMinutes, ct);
        }
        catch (DirectoryReadException e) when (e is DirectoryWriteException { Forbidden: true } || e.Category is not ("NotReplicated" or "GraphUnavailable" or "IdentityUnavailable"))
        {
            return work with { TapLeft = $"Entra did not create the Temporary Access Pass ({e.Category})" };
        }
        work = work with { TapIssuedAt = tap.IssuedAt, TapExpiresAt = tap.ExpiresAt };
        var (subject, body) = Messages.SignInDetails(r, scope, project, upn, password?.Reveal(), tap.Pass.Reveal(), tap.ExpiresAt, settings);
        var key = Ids.Key(r.Id, $"{task.Id}:sign-in");
        var masked = TapRecipient.Masked(to);
        try
        {
            await directMail!.SendNowAsync(new Notification
            {
                Id = Notification.MakeId(key, NotificationKind.SignInDetails), ProjectId = r.ProjectId, RequestId = r.Id, Kind = NotificationKind.SignInDetails,
                IdempotencyKey = key, To = [to], Subject = subject, Body = body, CreatedAt = clock.UtcNow, Status = NotificationStatus.Pending,
            }, ct);
        }
        catch (OutboundException e)
        {
            logger.LogWarning("sign-in details not sent {Category} request={RequestId}", e.Category, r.Id);
            await AuditTapAsync(r, user.ObjectId, "tap-mail-failed", $"sentTo={masked}; expiresAt={tap.ExpiresAt:O}; error={e.Category}", cid, ct);
            return work with { TapLeft = $"the sign-in details mail could not be sent ({e.Category}); the pass the agent created was never shown to anyone, so the ID team issues a new one (it replaces the old one)" };
        }
        var sent = work with { SignInDetailsSentAt = clock.UtcNow, SignInDetailsSentTo = masked, SignInDetailsFrom = check.Source, PasswordMailed = password is not null };
        signInDetailsSent[r.Id] = sent;
        // F2: the audit keeps only the user id, the masked recipient, the time, the expiry and the correlation id.
        await AuditTapAsync(r, user.ObjectId, "tap-mail-sent", $"sentTo={masked}; expiresAt={tap.ExpiresAt:O}; source={check.Source}", cid, ct);
        return sent;
    }

    private Task AuditTapAsync(AccessRequest r, string userObjectId, string action, string details, string cid, CancellationToken ct) =>
        stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(clock.UtcNow), ProjectId = r.ProjectId, At = clock.UtcNow, Actor = Agent, Action = action,
            Target = userObjectId, CorrelationId = cid, Details = details,
        }, ct);

    private static string Done(AgentWork w, AccessRequest r, ReviewedScope scope) =>
        $"AI agent: user {r.DomainId} {(w.UserCreated ? "created" : "exists")}; group {scope.EntraGroup} {(w.GroupCreated ? "created" : "exists")}" +
        $"{(w.MappingUpdated ? " (mapping sheet updated)" : "")}; membership {(w.MemberAdded ? "added" : "already in place")}.";

    /// <summary>Entra mail nickname from the Enterprise ID (ASCII letters, digits, dot, dash, underscore).</summary>
    private static string MailNickname(AccessRequest r)
    {
        var nick = NicknameUnsafe().Replace((r.EnterpriseId ?? r.DomainId!).Split('@')[0], "");
        return nick.Length == 0 ? $"user{r.Id[..8]}" : nick[..Math.Min(nick.Length, 64)];
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex NicknameUnsafe();

    private Step ToIdTeam(AccessRequest r, TeamTask task, AgentWork work, Project? project, DateTimeOffset now, IssueSeverity severity)
    {
        var flow = r.Flow!;
        var manualTask = task with { Agent = work with { At = now }, SinceAt = now };
        var next = (flow with { IdTeamTask = manualTask }).Waiting(WaitingOn.IdTeam, now)
            .Log(now, Agent, "agent-manual", $"Left to the ID team: {work.Manual}.");
        var (subject, body) = Messages.TeamTask(r, manualTask, settings);
        var effects = new List<Func<CancellationToken, Task>>
        {
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{task.Id}:manual", NotificationKind.TeamTask, Recipients.IdTeam(project), subject,
                $"The AI agent did not provision this request ({work.Manual}); please do it.\n\n{body}", c),
        };
        if (severity != IssueSeverity.Info)
            effects.Add(c => issues.RecordAsync(r.ProjectId, r.Id, $"{task.Id}:manual", IssueKind.NeedsDeveloper, severity,
                $"Agent provisioning left to the ID team: {work.Manual}.", null, c));
        return new Step(r with { Flow = next }, effects);
    }

    /// <summary>Transient failure (or not yet replicated): keep what is done, retry on the next run; after 3 attempts the team does it.</summary>
    private Step RetryOrTeam(AccessRequest r, TeamTask task, AgentWork work, string step, string category, Project? project, DateTimeOffset now, bool isIdTeam)
    {
        var flow = r.Flow!;
        var key = Ids.Key(r.Id, step);
        if (flow.AttemptsFor(key) >= settings.MaxAttempts - 1)
        {
            var reason = $"the agent's {(isIdTeam ? "provisioning" : "role assignment")} failed {settings.MaxAttempts} times ({category})";
            return isIdTeam
                ? ToIdTeam(r, task, work with { Manual = reason }, project, now, IssueSeverity.High)
                : ToRoleTeam(r, task, work with { Manual = reason }, project, now, IssueSeverity.High);
        }
        var kept = task with { Agent = work with { At = now } };
        var next = (isIdTeam ? flow with { IdTeamTask = kept } : flow with { RoleTeamTask = kept }).WithAttempt(key)
            .Log(now, Agent, "auto-retry", $"{(isIdTeam ? "Provisioning" : "Role assignment")} paused ({category}); retried on the next agent run.");
        return new Step(r with { Flow = next },
        [
            c => issues.RecordAsync(r.ProjectId, r.Id, step, IssueKind.DependencyFailure, IssueSeverity.Warning,
                $"Agent {(isIdTeam ? "provisioning" : "role assignment")} paused ({category}); retrying.", null, c, IssueStatus.AutoFixed),
        ], Stop: true);
    }

    /// <summary>
    /// A refused write (403) right after the permissions were granted is expected: a managed identity's token is cached for
    /// about 24 hours (F61), so new permissions can take hours to reach the agent. Keep what is done and retry every run for
    /// <see cref="WorkflowSettings.PermissionWait"/>; after that the team does it, with a High issue.
    /// </summary>
    private Step? RefusedOrTeam(AccessRequest r, TeamTask task, AgentWork work, string category, Project? project, DateTimeOffset now, bool isIdTeam)
    {
        var since = work.RefusedSince ?? now;
        var where = isIdTeam ? "Entra" : "Azure";
        if (now - since >= settings.PermissionWait)
        {
            var reason = $"{where} refused the agent's write ({category}) since {since:u}: the permission is missing";
            return isIdTeam
                ? ToIdTeam(r, task, work with { Manual = reason }, project, now, IssueSeverity.High)
                : ToRoleTeam(r, task, work with { Manual = reason }, project, now, IssueSeverity.High);
        }
        if (work.RefusedSince is not null && work == task.Agent) return null; // still waiting, nothing new to keep

        var flow = r.Flow!;
        var kept = task with { Agent = work with { At = now, RefusedSince = since } };
        var next = isIdTeam ? flow with { IdTeamTask = kept } : flow with { RoleTeamTask = kept };
        if (work.RefusedSince is not null) return new Step(r with { Flow = next }, [], Stop: true); // progress before the refusal

        var what = isIdTeam ? "provisioning" : "role assignment";
        next = next.Log(now, Agent, "agent-waiting-permission",
            $"{where} refused the agent's {what} write ({category}). A newly granted permission can take up to about 24 hours to reach the agent; " +
            $"retried every agent run until {since + settings.PermissionWait:u}, then the {(isIdTeam ? "ID team" : "role assignment team")} does it.");
        return new Step(r with { Flow = next },
        [
            c => issues.RecordAsync(r.ProjectId, r.Id, $"permission:{task.Id}", IssueKind.DependencyFailure, IssueSeverity.Warning,
                $"Agent {what} waiting for its permission ({category}); retried every run for up to {settings.PermissionWait.TotalHours:0} h, then the team does it.", null, c),
        ], Stop: true);
    }

    // ---- Stage 5: Azure role on the group --------------------------------------------------------------------------

    private async Task<Step?> AssignRoleAsync(AccessRequest r, TeamTask task, CancellationToken ct)
    {
        var now = clock.UtcNow;
        r = Resumed(r, task, now);
        var flow = r.Flow!;
        var scope = flow.Scope!;
        var project = await ProjectAsync(r, ct);
        var row = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var work = (task.Agent ?? new AgentWork { At = now }) with { Manual = null };
        // Exactly the Azure roles the Owner and Security saw: taken from the approved scope, never from the live row.
        var grants = scope.Grants();

        // v1.2: a Databricks row (role scope dbx:…) is set up by the agent only with AGENT_DATABRICKS; it never carries Azure roles
        // too (one row per plane), so a request is never completed with only half of its access in place.
        var databricksRow = Databricks.DatabricksPolicy.IsDatabricksScope(scope.RoleScope);
        var manual = !BothApprovalsCurrent(flow) ? "the Owner and Security approvals are not both current (not assigned)"
            : Tenants.NotConnected(project, settings.AgentTenantId)
            ?? (row is null ? $"mapping row {scope.MappingRowId} not found"
            : PrivilegedReason(row, scope)
            ?? (databricksRow && grants.Count > 0 ? DatabricksMixed
            : databricksRow && databricks is null ? DatabricksOff
            : grants.Count == 0 && !databricksRow ? "the approved access has no Azure role and scope for the agent (e.g. a Databricks role)" : null));
        var severity = manual?.Contains("not assigned", StringComparison.Ordinal) == true ? IssueSeverity.High : IssueSeverity.Info;
        if (manual is null)
        {
            // All or nothing: the agent assigns the group's roles only when every one is resolved and allow-listed, so
            // the role team never finishes half of an assignment the agent started.
            var unresolved = grants.Where(g => g.RoleId is null || g.Scope is null).ToList();
            var notAllowed = grants.Except(unresolved).Where(g => !AllowListed(g)).ToList();
            if (notAllowed.Count > 0)
            {
                manual = $"{notAllowed.Count} of {grants.Count} Azure role(s) {NotAllowListed}: " +
                         string.Join("; ", notAllowed.Select(g => $"{g.RoleName} at {g.Scope}"));
                severity = IssueSeverity.High;
            }
            else if (unresolved.Count > 0)
            {
                manual = $"{unresolved.Count} of {grants.Count} Azure role(s) have no resolved role id or scope: " +
                         string.Join("; ", unresolved.Select(g => $"{g.RoleName} on {g.Where}"));
            }
        }
        if (manual is not null) return ToRoleTeam(r, task, work with { Manual = manual }, project, now, severity);
        if (databricksRow) return await DatabricksSetupAsync(r, task, scope, work, project, now, ct);

        try
        {
            var groupId = flow.IdTeamTask?.Agent?.GroupObjectId ?? flow.IdTeamTask?.IdReport?.GroupObjectId ?? row?.GroupObjectId;
            if (groupId is null)
            {
                var groups = await directory!.FindGroupsAsync(scope.EntraGroup, ct);
                if (groups.Count != 1)
                    return ToRoleTeam(r, task, work with { Manual = $"group {scope.EntraGroup} is not unique in Entra ({groups.Count} found)" }, project, now, IssueSeverity.High);
                groupId = groups[0].ObjectId;
            }
            // Idempotent: a repeat (retry after a refusal part-way) finds the earlier assignments and counts them as done.
            foreach (var g in grants) await roleWriter!.AssignAsync(groupId, "Group", g.RoleId!, g.Scope!, ct);
        }
        catch (DirectoryWriteException e) when (e.Forbidden)
        {
            return RefusedOrTeam(r, task, work, e.Category, project, now, isIdTeam: false);
        }
        catch (DirectoryReadException e)
        {
            return RetryOrTeam(r, task, work, $"role-assign:{task.Id}", e.Category, project, now, isIdTeam: false);
        }

        // Same record the role team would write through its form.
        var roles = ExpectedRoles(scope);
        await UpdateResponseAsync(r, x => x with { AssignedRoles = roles, State = ResponseState.Assigned, AssignedBy = AgentName, AssignedAt = now }, ct);
        var list = string.Join("; ", grants.Select(g => $"{g.RoleName} at {g.Scope}")) + (row?.Privileged == true ? " — privileged access row" : "");
        var reported = task with
        {
            Agent = work with { At = now, RoleAssigned = true, Completed = true },
            State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = AgentName,
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? AgentName,
            RoleReport = new RoleTeamReport { AssignedRoles = roles, Notes = $"Assigned by the AI agent to group {scope.EntraGroup}: {list}." },
        };
        var next = (flow with { RoleTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, Agent, "agent-role-assigned", $"AI agent assigned {grants.Count} Azure role(s) to group {scope.EntraGroup} ({list}) and updated the response document.");
        // No mail of its own: the agent verifies the access next and the completion mail names the roles and who assigned
        // them (mail budget, F70). The trail records the assignment now.
        var effects = new List<Func<CancellationToken, Task>>();
        if (work.RefusedSince is not null) effects.Add(c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.DependencyFailure], c)); // the permission arrived
        if (task.Agent?.Manual is not null) effects.Add(c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.NeedsDeveloper], c)); // taken back and done
        return new Step(r with { Flow = next }, effects);
    }

    /// <summary>What the role assignment must show, exactly: every approved Azure role at its scope, else the mapped role.</summary>
    public static IReadOnlyList<AssignedRole> ExpectedRoles(ReviewedScope scope)
    {
        var grants = scope.Grants();
        return grants.Count == 0 ? [new AssignedRole(scope.Role, scope.RoleScope)] : grants.Select(g => new AssignedRole(g.RoleName, g.Where)).ToList();
    }

    /// <summary>Scope prefix match on whole path segments (…/rg-a does not allow …/rg-ab).</summary>
    public static bool InScope(string scope, string allowed)
    {
        var s = scope.TrimEnd('/');
        var a = allowed.TrimEnd('/');
        return a.Length > 0 && (s.Equals(a, StringComparison.OrdinalIgnoreCase) || s.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));
    }

    private Step ToRoleTeam(AccessRequest r, TeamTask task, AgentWork work, Project? project, DateTimeOffset now, IssueSeverity severity)
    {
        var flow = r.Flow!;
        var manualTask = task with { Agent = work with { At = now }, SinceAt = now };
        var next = (flow with { RoleTeamTask = manualTask }).Waiting(WaitingOn.RoleTeam, now)
            .Log(now, Agent, "agent-manual", $"Role assignment left to the role assignment team: {work.Manual}.");
        var (subject, body) = Messages.TeamTask(r, manualTask, settings);
        var effects = new List<Func<CancellationToken, Task>>
        {
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, task.Id, NotificationKind.TeamTask, Recipients.RoleTeam(project), subject, body, c),
        };
        if (severity != IssueSeverity.Info)
            effects.Add(c => issues.RecordAsync(r.ProjectId, r.Id, $"{task.Id}:manual", IssueKind.NeedsDeveloper, severity,
                $"Agent role assignment left to the role assignment team: {work.Manual}.", null, c));
        return new Step(r with { Flow = next }, effects);
    }

    private async Task UpdateResponseAsync(AccessRequest r, Func<ResponseRecord, ResponseRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await stores.ResponseDocument.GetAsync(r.ProjectId, r.Id, ct);
            if (existing is null) return;
            try
            {
                await stores.ResponseDocument.ReplaceAsync(change(existing.Value) with
                {
                    UpdatedBy = Agent, UpdatedAt = clock.UtcNow, Version = existing.Value.Version + 1,
                }, existing.ETag, ct);
                return;
            }
            catch (ConcurrencyException)
            {
                // Changed meanwhile; re-read and apply again.
            }
        }
    }
}
