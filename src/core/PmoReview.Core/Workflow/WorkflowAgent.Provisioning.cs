using System.Text.RegularExpressions;
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
/// Access Pass stays with the ID team. Anything outside the safe path goes to the team with the reason and an issue.
/// </summary>
public sealed partial class WorkflowAgent
{
    public const string AgentName = "AI agent";

    private bool ProvisioningPending(TeamTask task) =>
        settings.AgentProvisioning && task.State == TeamTaskState.Open && task.Agent is not ({ Completed: true } or { Manual: not null });

    private bool RoleAssignmentPending(TeamTask task) =>
        settings.AgentRoleAssignment && task.State == TeamTaskState.Open && task.Agent is null;

    private bool BothApprovalsCurrent(WorkflowState flow) =>
        flow.CurrentApproval(ApprovalStage.Owner)?.Decision == ApprovalDecision.Approved
        && flow.CurrentApproval(ApprovalStage.Security)?.Decision == ApprovalDecision.Approved;

    // ---- Stage 4: user, group, membership --------------------------------------------------------------------------

    private async Task<Step> ProvisionAsync(AccessRequest r, TeamTask task, CancellationToken ct)
    {
        var flow = r.Flow!;
        var scope = flow.Scope!;
        var now = clock.UtcNow;
        var project = await ProjectAsync(r, ct);
        var sheet = await sources.ReadMappingSheetAsync(r.ProjectId, ct);
        var row = sheet.FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var work = task.Agent ?? new AgentWork { At = now };

        if (!BothApprovalsCurrent(flow))
            return ToIdTeam(r, task, work with { Manual = "the Owner and Security approvals are not both current" }, project, now, IssueSeverity.High);
        var manual = r.Guest ? "guest accounts are invited by the ID team, not created by the agent"
            : row?.Privileged == true ? $"mapping row {scope.MappingRowId} is privileged access"
            : null;
        if (manual is not null) return ToIdTeam(r, task, work with { Manual = manual }, project, now, IssueSeverity.Info);

        try
        {
            var user = await directory!.FindUserAsync(r.DomainId!, ct);
            if (user is null)
            {
                user = await writer!.CreateUserAsync(new NewUser(r.DomainId!.ToLowerInvariant(), $"{r.FirstName} {r.LastName}".Trim(),
                    MailNickname(r), r.FirstName, r.LastName), ct);
                work = work with { UserCreated = true };
            }
            else if (user.IsGuest)
            {
                return ToIdTeam(r, task, work with { Manual = $"{r.DomainId} is a guest account" }, project, now, IssueSeverity.Info);
            }
            work = work with { UserObjectId = user.ObjectId };

            if (work.GroupObjectId is null)
            {
                // A group the agent created is used by id afterwards: a name filter can lag right after creation.
                var groups = await directory.FindGroupsAsync(scope.EntraGroup, ct);
                if (groups.Count > 1)
                    return ToIdTeam(r, task, work with { Manual = $"{groups.Count} groups are named {scope.EntraGroup} in Entra" }, project, now, IssueSeverity.High);
                if (groups.Count == 1 && !groups[0].SecurityEnabled)
                    return ToIdTeam(r, task, work with { Manual = $"{scope.EntraGroup} is not a security group" }, project, now, IssueSeverity.High);
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

            if (!await directory.IsMemberAsync(work.GroupObjectId!, user.ObjectId, ct))
            {
                await writer!.AddMemberAsync(work.GroupObjectId!, user.ObjectId, ct);
                work = work with { MemberAdded = true };
            }
            work = work with { Membership = true };
        }
        catch (DirectoryWriteException e) when (e.Forbidden)
        {
            return ToIdTeam(r, task, work with { Manual = $"Entra refused the agent's write ({e.Category}): the permission is missing" }, project, now, IssueSeverity.High);
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
        var done = Done(work, r, scope);
        if (work.UserCreated)
        {
            // A new account needs a one-time TAP for the first sign-in: only the ID team issues it.
            var tapTask = task with { Agent = work, Instructions = Messages.IdTeamTapInstructions(r, newScope, flow, done, settings.TapMaxLifetimeMinutes), SinceAt = now };
            var next = (flow with { Scope = newScope, IdTeamTask = tapTask }).Waiting(WaitingOn.IdTeam, now)
                .Log(now, Agent, "agent-provisioned", $"{done} The ID team issues the one-time Temporary Access Pass.");
            var (subject, body) = Messages.TeamTask(r, tapTask, settings);
            return new Step(r with { Flow = next },
                [c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{tapTask.Id}:tap", NotificationKind.TeamTask, Recipients.IdTeam(project), subject, body, c)], Stop: true);
        }

        // Existing account: nothing left for the ID team. Verified on the next run (Entra reads lag right after a write).
        var report = new IdTeamReport
        {
            UserPrincipalName = r.DomainId!.ToLowerInvariant(), UserObjectId = work.UserObjectId, GroupName = scope.EntraGroup,
            GroupObjectId = work.GroupObjectId, GroupCreated = work.GroupCreated, UserCreated = false, AddedToGroup = true,
            MappingSheetUpdated = work.MappingUpdated, Notes = "Done by the AI agent after Owner and Security approval; existing account, no TAP needed.",
        };
        var reported = task with
        {
            Agent = work, State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = AgentName, IdReport = report,
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? AgentName,
        };
        var verify = (flow with { Scope = newScope, IdTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, Agent, "agent-provisioned", $"{done} Existing account: no TAP needed. Verified on the next agent run.");
        return new Step(r with { Flow = verify }, [], Stop: true);
    }

    private static string Done(AgentWork w, AccessRequest r, ReviewedScope scope) =>
        $"AI agent: user {r.DomainId} {(w.UserCreated ? "created" : "exists")}; group {scope.EntraGroup} {(w.GroupCreated ? "created" : "exists")}" +
        $"{(w.MappingUpdated ? " (mapping sheet updated)" : "")}; membership {(w.MemberAdded ? "added" : "already in place")}.";

    /// <summary>Entra mail nickname from the Enterprise ID (ASCII letters, digits, dot, dash, underscore).</summary>
    private static string MailNickname(AccessRequest r)
    {
        var nick = NicknameUnsafe().Replace(r.EnterpriseId ?? r.DomainId!.Split('@')[0], "");
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

    // ---- Stage 5: Azure role on the group --------------------------------------------------------------------------

    private async Task<Step> AssignRoleAsync(AccessRequest r, TeamTask task, CancellationToken ct)
    {
        var flow = r.Flow!;
        var scope = flow.Scope!;
        var now = clock.UtcNow;
        var project = await ProjectAsync(r, ct);
        var row = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var work = task.Agent ?? new AgentWork { At = now };
        var roleId = row?.AzureRoleDefinitionId;
        var azureScope = row?.AzureScope;

        var manual = !BothApprovalsCurrent(flow) ? "the Owner and Security approvals are not both current (not assigned)"
            : row is null ? $"mapping row {scope.MappingRowId} not found"
            : row.Privileged ? $"mapping row {scope.MappingRowId} is privileged access"
            : roleId is null || azureScope is null ? "the mapping row has no Azure role and scope for the agent (e.g. a Databricks role)"
            : !settings.RoleAssignAllowedRoleIds.Contains(roleId, StringComparer.OrdinalIgnoreCase) ? $"Azure role {roleId} is not on the agent's allow-list"
            : !settings.RoleAssignScopes.Any(s => InScope(azureScope, s)) ? $"scope {azureScope} is not on the agent's allow-list"
            : null;
        if (manual is not null)
        {
            var severity = manual.Contains("allow-list", StringComparison.Ordinal) || manual.Contains("not assigned", StringComparison.Ordinal)
                ? IssueSeverity.High : IssueSeverity.Info;
            return ToRoleTeam(r, task, work with { Manual = manual }, project, now, severity);
        }

        try
        {
            var groupId = flow.IdTeamTask?.Agent?.GroupObjectId ?? flow.IdTeamTask?.IdReport?.GroupObjectId;
            if (groupId is null)
            {
                var groups = await directory!.FindGroupsAsync(scope.EntraGroup, ct);
                if (groups.Count != 1)
                    return ToRoleTeam(r, task, work with { Manual = $"group {scope.EntraGroup} is not unique in Entra ({groups.Count} found)" }, project, now, IssueSeverity.High);
                groupId = groups[0].ObjectId;
            }
            await roleWriter!.AssignAsync(groupId, "Group", roleId!, azureScope!, ct);
        }
        catch (DirectoryWriteException e) when (e.Forbidden)
        {
            return ToRoleTeam(r, task, work with { Manual = $"Azure refused the agent's role assignment ({e.Category})" }, project, now, IssueSeverity.High);
        }
        catch (DirectoryReadException e)
        {
            return RetryOrTeam(r, task, work, $"role-assign:{task.Id}", e.Category, project, now, isIdTeam: false);
        }

        // Same record the role team would write through its form.
        var roles = new List<AssignedRole> { new(scope.Role, scope.RoleScope) };
        await UpdateResponseAsync(r, x => x with { AssignedRoles = roles, State = ResponseState.Assigned, AssignedBy = AgentName, AssignedAt = now }, ct);
        var reported = task with
        {
            Agent = work with { At = now, RoleAssigned = true, Completed = true },
            State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = AgentName,
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? AgentName,
            RoleReport = new RoleTeamReport { AssignedRoles = roles, Notes = $"Assigned by the AI agent: Azure role {roleId} at {azureScope} to group {scope.EntraGroup}." },
        };
        var next = (flow with { RoleTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, Agent, "agent-role-assigned", $"AI agent assigned {scope.Role} (Azure role {roleId}) at {azureScope} to group {scope.EntraGroup} and updated the response document.");
        return new Step(r with { Flow = next }, []);
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
