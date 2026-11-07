using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Http;
using PmoReview.Core.Resources;
using PmoReview.Core.Security;
using PmoReview.Core.Services;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

public sealed record ApprovalInput(ApprovalStage? Stage, ApprovalDecision? Decision, string? Remarks, string? ReviewRunId);

public sealed record TapInput(DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt, bool? OneTime, string? IssuedBy);

public sealed record IdTeamReportInput
{
    public string? UserPrincipalName { get; init; }
    public string? UserObjectId { get; init; }
    public string? GroupName { get; init; }
    public string? GroupObjectId { get; init; }
    public bool? UserCreated { get; init; }
    public bool? GroupCreated { get; init; }
    public bool? AddedToGroup { get; init; }
    public bool? MappingSheetUpdated { get; init; }
    /// <summary>Metadata only. There is no field for the pass itself.</summary>
    public TapInput? Tap { get; init; }
    public string? Notes { get; init; }
}

public sealed record AssignedRoleInput(string? Role, string? Scope);

public sealed record RoleTeamReportInput(IReadOnlyList<AssignedRoleInput>? AssignedRoles, string? Notes);

/// <summary>What the caller can do on this request right now (computed server-side; the UI only shows it).</summary>
public sealed record RequestView(
    Item<AccessRequest> Request,
    StatusLine Status,
    Timings Timings,
    IReadOnlyList<string> Actions,
    IReadOnlyList<Notification> Notifications,
    IReadOnlyList<Issue> Issues,
    IReadOnlyList<string> FlowOverview,
    string WhoDoesWhat)
{
    /// <summary>Why the caller sees no decision while the request waits for an approval (wrong person, missing role, …).</summary>
    public string? ApprovalHint { get; init; }
    /// <summary>The open Prod escalation of a request at Awaiting Prod approval (7 Nov 2026: decided on the request page too).</summary>
    public Escalation? ProdEscalation { get; init; }
}

public sealed record ActionItem(StatusLine Status, string Action);

/// <param name="Reason">Why (required for a real request; kept in the audit trail).</param>
/// <param name="Confirm">Must be true: deleting cannot be undone.</param>
public sealed record DeleteRequestInput(string? Reason, bool? Confirm);

public sealed record DeletedRequest(string RequestId, bool Simulated, string StatusAtDeletion, int ReviewRunsRemoved, int NotificationsRemoved, int IssuesRemoved,
    int EscalationsRemoved, bool ResponseRecordKept, string Note);

public static class Actions
{
    public const string ApproveOwner = "ApproveOwner";
    public const string ApproveSecurity = "ApproveSecurity";
    public const string IdTeamTask = "IdTeamTask";
    public const string RoleTeamTask = "RoleTeamTask";
    public const string Resubmit = "Resubmit";
    /// <summary>Developer: delete this request (4 Nov 2026).</summary>
    public const string Delete = "Delete";
    /// <summary>The Prod stage: a ProdApprover, or the developer on a simulation (7 Nov 2026: on the request page, not only Escalations).</summary>
    public const string DecideProd = "DecideProd";
}

/// <summary>
/// The human side of the flow: submit, Owner and Security approvals, team actions, and the status views. Every action
/// is authorised from the platform identity, tied to the request id, and followed by an agent step.
/// </summary>
public sealed partial class WorkflowService(
    DataStores stores,
    RequestService requests,
    WorkflowAgent agent,
    ISourcesOfTruth sources,
    Notifier notifier,
    IssueLog issues,
    WorkflowSettings settings,
    IClock clock,
    ILogger<WorkflowService> logger)
{
    public WorkflowSettings Settings => settings;

    /// <summary>
    /// The permission for this action on this request: the role's own, or — on a developer's simulation only — Simulate
    /// (the developer acts for every stage; nothing is granted). Throws like <see cref="CallerContext.Require"/>.
    /// </summary>
    public static void RequireFor(CallerContext caller, AccessRequest r, Permission permission)
    {
        if (!Can(caller, r, permission)) caller.Require(r.ProjectId, permission);
    }

    public static bool Can(CallerContext caller, AccessRequest r, Permission permission) =>
        caller.Has(r.ProjectId, permission) || (r.Simulated && caller.Has(r.ProjectId, Permission.Simulate));

    /// <summary>A developer's simulation: submitted like a request, run with nothing written to Entra or Azure.</summary>
    public Task<RequestView> SimulateAsync(CallerContext caller, string projectId, RequestInput input, string cid, CancellationToken ct) =>
        SubmitAsync(caller, projectId, input, cid, ct, simulated: true);

    /// <summary>
    /// Developer (4 Nov 2026, Gokul: "requests can be deleted"): deletes a request with its review runs, mails and issues; an
    /// audit event keeps who, when, why and what it was. A real request needs a reason. Deleting does not remove access already
    /// granted in Entra or Azure (Offboard user does), so a real request's response document record stays once it was approved.
    /// </summary>
    public async Task<DeletedRequest> DeleteAsync(CallerContext caller, string projectId, string requestId, DeleteRequestInput input, string cid, CancellationToken ct)
    {
        caller.Require(projectId, Permission.DeleteRequests);
        var r = (await requests.LoadAsync(projectId, requestId, ct)).Value;
        if (input.Confirm != true) throw AppException.Invalid("confirm must be true: deleting a request cannot be undone.");
        var reason = SecretMasker.Apply(RequestService.Text(input.Reason, "reason", 500));
        if (!r.DryRun && (reason is null || reason.Length < 5))
            throw AppException.Invalid("A real request or a live simulation needs a reason (at least 5 characters): it is kept in the audit trail.");
        // Review 7 Nov 2026, finding 5: a live simulation is what makes its test account the project's own, so Offboard user
        // can remove it. Deleted while its access stands, nothing could offboard it any more: offboard first.
        if (r.Simulated && !r.DryRun && (await stores.ResponseDocument.GetAsync(projectId, requestId, ct))?.Value is { State: ResponseState.Approved or ResponseState.Assigned })
            throw AppException.Conflict("offboard_first",
                "This live simulation's access is still granted in Entra or Azure. Remove it with Offboard user first (the request page, PMO or Operator), then delete the simulation.");
        var status = r.Flow is null ? "legacy v1" : StatusText.Label(r.Flow.Status);

        // The request first, so the agent stops at once (its next save finds nothing); then what belongs to it.
        await stores.Requests.DeleteAsync(projectId, requestId, ct);
        async Task<int> RemoveAsync<T>(IDocumentStore<T> store, Func<T, bool> mine) where T : class, IDocument
        {
            var n = 0;
            foreach (var d in (await store.ListAsync(projectId, ct)).Select(x => x.Value).Where(mine))
                if (await store.DeleteAsync(d.PartitionKey, d.Id, ct)) n++;
            return n;
        }
        var runs = await RemoveAsync(stores.Runs, x => x.RequestId == requestId);
        var mails = await RemoveAsync(stores.Notifications, x => x.RequestId == requestId);
        var issueCount = await RemoveAsync(stores.Issues, x => x.RequestId == requestId);
        // Request-bound escalations: all of a simulation; of a real request only an undecided Prod one (nothing left to decide).
        var escalations = await RemoveAsync(stores.Escalations, x => x.RequestId == requestId
            && (r.DryRun || (x.Kind == EscalationKind.ProdWrite && x.Status == EscalationStatus.Open)));
        var record = (await stores.ResponseDocument.GetAsync(projectId, requestId, ct))?.Value;
        var keep = record is not null && record.State is not (ResponseState.PendingApproval or ResponseState.Rejected);
        if (record is not null && !keep) await stores.ResponseDocument.DeleteAsync(projectId, requestId, ct);

        var what = $"{(r.Simulated ? "simulation" : "request")} {requestId} at {status} ({r.AccessLevel} on {r.Resource}{(r.Environment is null ? "" : $", {r.Environment}")}, {r.DomainId})";
        await stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(clock.UtcNow), ProjectId = projectId, At = clock.UtcNow, Actor = caller.DisplayName, Action = "request-deleted",
            Target = $"request {requestId}", CorrelationId = cid,
            Details = $"Deleted {what}. Reason: {reason ?? "-"}. Removed {runs} review run(s), {mails} mail(s), {issueCount} issue(s), {escalations} escalation(s); " +
                      (record is null ? "no response document record." : keep ? "response document record kept (access may have been granted)." : "response document record removed."),
        }, ct);
        logger.LogInformation("request deleted request={RequestId} simulated={Simulated} by={UserId} cid={Cid}", requestId, r.Simulated, caller.UserId, cid);
        return new DeletedRequest(requestId, r.Simulated, status, runs, mails, issueCount, escalations, keep,
            r.DryRun ? "Simulation deleted." : r.Simulated ? "Live simulation deleted. Its access had been offboarded or never granted; the response document keeps its record." : "Request deleted. Access already granted in Entra or Azure stays: use Offboard user to remove it.");
    }

    // ---- Stage 1 ------------------------------------------------------------------------------------------------

    /// <param name="advance">False: saved as Submitted only; the worker starts the review on its next run (bundle time budget).</param>
    public async Task<RequestView> SubmitAsync(CallerContext caller, string projectId, RequestInput input, string cid, CancellationToken ct, string? bundleId = null,
        bool advance = true, bool simulated = false)
    {
        var created = await requests.CreateAsync(caller, projectId, input, ct, bundleId, simulated);
        if (advance) await AdvanceSavedAsync(created.Value, cid, ct);
        return await ViewAsync(caller, projectId, created.Value.Id, ct);
    }

    public async Task<RequestView> ResubmitAsync(CallerContext caller, string projectId, string requestId, RequestInput input, string? ifMatch, string cid, CancellationToken ct)
    {
        var saved = await requests.ResubmitAsync(caller, projectId, requestId, input, ifMatch, ct);
        await AdvanceSavedAsync(saved.Value, cid, ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    /// <summary>
    /// The request is already saved: an agent error here goes on the issues list and the worker retries, instead of a
    /// 500 that makes the requester submit the same request again.
    /// </summary>
    private Task AdvanceSavedAsync(AccessRequest saved, string cid, CancellationToken ct) =>
        AfterSavedAsync(saved.ProjectId, saved.Id, StatusText.Label(saved.Flow?.Status ?? RequestStatus.Submitted), c => agent.AdvanceAsync(saved.ProjectId, saved.Id, cid, c), ct);

    /// <summary>
    /// The person's action is saved: what follows (the response document, mails, the agent step) must not turn it into an
    /// error page. Before 4 Nov 2026 an error there gave the Owner a 500 although the approval counted, Security was never
    /// asked, and the Owner's retry was refused as "not an approver". Now it goes on the issues list and the agent repeats
    /// it (an approval request is asked again on its next run; the worker continues every open request).
    /// </summary>
    private async Task AfterSavedAsync(string projectId, string requestId, string where, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            await work(ct);
        }
        catch (Exception e) when (e is not AppException && Failures.NotCancelled(e, ct))
        {
            await agent.RecordErrorAsync(projectId, requestId, where, e, ct);
        }
    }

    // ---- Stage 3: approvals -------------------------------------------------------------------------------------

    public async Task<RequestView> ApproveAsync(CallerContext caller, string projectId, string requestId, ApprovalInput input, string cid, CancellationToken ct)
    {
        if (!caller.InProject(projectId))
        {
            logger.LogWarning("approval attempt without project access request={RequestId} user={UserId}", requestId, caller.UserId);
            throw AppException.NotFound("Project");
        }
        var current = await requests.LoadAsync(projectId, requestId, ct);
        var r = current.Value;
        var flow = r.Flow ?? throw AppException.Conflict("legacy_request", "This is a v1 request; it has no approval stage.");
        var stage = flow.Status switch
        {
            RequestStatus.AwaitingOwner => ApprovalStage.Owner,
            RequestStatus.AwaitingSecurity => ApprovalStage.Security,
            _ => throw AppException.Conflict("not_awaiting_approval", $"The request is {StatusText.Label(flow.Status)}; no approval is pending."),
        };
        if (input.Stage is { } asked && asked != stage)
            throw AppException.Conflict("wrong_stage", $"The request is waiting for the {stage} approval, not {asked}.");

        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value;
        // A simulation: the developer acts for the Owner and Security (nothing is granted from it).
        var authorized = (r.Simulated && caller.Has(projectId, Permission.Simulate)) || (stage == ApprovalStage.Owner
            ? caller.Has(projectId, Permission.ApproveAsOwner) && caller.Is(r.Owner)
            : caller.Has(projectId, Permission.ApproveAsSecurity));
        if (!authorized)
        {
            // Nobody else approves: ignore it, log it and alert Security.
            var step = $"unauthorized-approval:{caller.UserId}:{stage}:{flow.Scope?.ReviewRunId}";
            await issues.RecordAsync(projectId, requestId, step, IssueKind.UnauthorizedApproval, IssueSeverity.High,
                $"{caller.DisplayName} (not {(stage == ApprovalStage.Owner ? "the named Owner" : "a Security approver")}) tried to respond at the {stage} stage. Ignored.", null, ct);
            var (subject, body) = Messages.SecurityAlert(r, caller.DisplayName, caller.UserId, stage, clock.UtcNow);
            await notifier.EnqueueAsync(projectId, requestId, step, NotificationKind.SecurityAlert, Recipients.Security(project), subject, body, ct);
            throw new AppException(ErrorKind.Forbidden, "not_an_approver",
                $"Your response was not counted: only {(stage == ApprovalStage.Owner ? "the Owner named on the request" : "a Security approver")} can approve at this stage. Security has been alerted.");
        }

        var decision = input.Decision ?? throw AppException.Invalid("decision is required (Approved or Rejected).");
        var remarks = SecretMasker.Apply(RequestService.Text(input.Remarks, "remarks", 1000));
        if (decision == ApprovalDecision.Rejected && remarks is null) throw AppException.Invalid("Remarks are required when rejecting.");
        if (flow.Scope is null || input.ReviewRunId != flow.Scope.ReviewRunId)
            throw AppException.Conflict("stale_review", "The AI review changed since you opened this page. Refresh and read the current summary before deciding.");
        if (stage == ApprovalStage.Security && !r.Simulated && flow.CurrentApproval(ApprovalStage.Owner)?.By == caller.UserId)
            throw AppException.Conflict("same_approver", "You gave the Owner approval. The Security approval must come from a different person.");

        // Mapping sheet changed after the AI review (or the reviewed row was retired or ended): re-run the review before continuing.
        var sheet = await sources.ReadMappingSheetAsync(projectId, ct);
        if (!MappingSheet.AsReviewed(sheet, r.Resource, flow.Scope.SheetFingerprint, flow.Scope.MappingRowId, clock.UtcNow))
        {
            await agent.AdvanceAsync(projectId, requestId, cid, ct);
            throw AppException.Conflict("mapping_changed", "The mapping sheet changed after the AI review. The review is being re-run; your response was not recorded. Refresh and read the new summary.");
        }

        var now = clock.UtcNow;
        var approval = new Approval
        {
            Stage = stage, Decision = decision, By = caller.UserId, ByName = caller.DisplayName, At = now, Remarks = remarks, ReviewRunId = flow.Scope.ReviewRunId,
        };
        var next = flow with { Approvals = [.. flow.Approvals, approval] };
        var effects = new List<Func<CancellationToken, Task>>();
        Func<ResponseRecord, ResponseRecord> record;

        if (decision == ApprovalDecision.Rejected)
        {
            next = (next with { LastReason = remarks })
                .To(RequestStatus.Rejected, WaitingOn.None, now, caller.UserId, $"{stage.ToString().ToLowerInvariant()}-rejected", $"Rejected by {stage}: {remarks}");
            record = x => x with { State = ResponseState.Rejected };
            var (subject, body) = Messages.Rejected(r, approval, settings);
            effects.Add(c => notifier.EnqueueAsync(projectId, requestId, $"rejected:{stage}", NotificationKind.Rejected,
                Recipients.Requester(r).Concat(Recipients.Pmo(project)), subject, body, c));
        }
        else if (stage == ApprovalStage.Owner)
        {
            next = next.To(RequestStatus.AwaitingSecurity, WaitingOn.Security, now, caller.UserId, "owner-approved", "Owner approved the business need. Sent to Security.");
            record = x => x with { OwnerApprovedBy = caller.DisplayName, OwnerApprovedAt = now };
            var withApproval = next;
            effects.Add(c => agent.AskApprovalAsync(r, ApprovalStage.Security, flow.Scope, withApproval, project, c));
        }
        else
        {
            // F3: Prod above Read waits for a ProdApprover; nothing is granted before that decision.
            var row = sheet.FirstOrDefault(x => x.RowId == flow.Scope.MappingRowId);
            if (ProdGuard.AboveReadInProd(r, flow.Scope, row) is { } prodReason)
            {
                var escalation = agent.NewProdEscalation(r, flow.Scope, prodReason, cid);
                next = next.To(RequestStatus.AwaitingProdApproval, WaitingOn.ProdApprover, now, caller.UserId, "security-approved",
                    $"Security approved risk and access level. Production above Read ({prodReason}): nothing is granted until a ProdApprover approves escalation {escalation.Id}.");
                record = x => x with { SecurityApprovedBy = caller.DisplayName, SecurityApprovedAt = now };
                var scope = flow.Scope;
                effects.Add(c => agent.RaiseProdEscalationAsync(r, scope, escalation, project, c));
            }
            else
            {
                var (fulfil, fx) = agent.ToIdTeam(r, next, project, caller.UserId, "security-approved", "Security approved risk and access level.");
                next = fulfil;
                effects.AddRange(fx);
                record = x => x with { SecurityApprovedBy = caller.DisplayName, SecurityApprovedAt = now, State = ResponseState.Approved };
            }
        }

        await SaveAsync(current, r with { Flow = next, UpdatedAt = now }, ct);
        await AfterSavedAsync(projectId, requestId, $"{stage} approval", async c =>
        {
            await UpdateResponseRecordAsync(projectId, requestId, caller.UserId, record, c);
            // The decision ends the wait: an "approval past SLA" issue for this request is closed (it stays in the daily counts).
            effects.Add(x => issues.ResolveAsync(projectId, requestId, [IssueKind.ApprovalSlaBreached], x));
            foreach (var e in effects) await e(c);
            await agent.AdvanceAsync(projectId, requestId, cid, c);
        }, ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    // ---- Stages 4 and 5: team actions ---------------------------------------------------------------------------

    public static AppRole TeamFromRoute(string team) => team switch
    {
        "id-team" => AppRole.IdTeam,
        "role-team" => AppRole.RoleTeam,
        _ => throw AppException.NotFound("Team"),
    };

    public async Task<RequestView> PickUpAsync(CallerContext caller, string projectId, string requestId, AppRole team, CancellationToken ct)
    {
        if (!caller.InProject(projectId)) throw AppException.NotFound("Project");
        var current = await requests.LoadAsync(projectId, requestId, ct);
        RequireFor(caller, current.Value, team == AppRole.IdTeam ? Permission.WorkIdTeamTasks : Permission.WorkRoleTeamTasks);
        var (flow, task) = OpenTask(current.Value, team);
        if (task.PickedUpAt is null)
        {
            var now = clock.UtcNow;
            var picked = task with { PickedUpAt = now, PickedUpBy = caller.DisplayName };
            var next = (team == AppRole.IdTeam ? flow with { IdTeamTask = picked } : flow with { RoleTeamTask = picked })
                .Log(now, caller.UserId, $"{Route(team)}-picked-up", $"{Messages.Team(team)} picked up the task ({caller.DisplayName}).");
            await SaveAsync(current, current.Value with { Flow = next, UpdatedAt = now }, ct);
        }
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    /// <summary>
    /// The team gives a task the AI agent left to it back to the agent (e.g. once privileged rows are switched on, the
    /// group object id is corrected or a permission arrived), picked up or not. Not an approval: the agent runs every
    /// check again on its next run (both approvals, the row, the never-list) and, if a reason still holds, leaves it again.
    /// </summary>
    public async Task<RequestView> HandBackAsync(CallerContext caller, string projectId, string requestId, AppRole team, CancellationToken ct)
    {
        if (!caller.InProject(projectId)) throw AppException.NotFound("Project");
        var current = await requests.LoadAsync(projectId, requestId, ct);
        RequireFor(caller, current.Value, team == AppRole.IdTeam ? Permission.WorkIdTeamTasks : Permission.WorkRoleTeamTasks);
        var (flow, task) = OpenTask(current.Value, team);
        if (!(team == AppRole.IdTeam ? settings.AgentProvisioning : settings.AgentRoleAssignment))
            throw AppException.Conflict("agent_off", $"The AI agent's {(team == AppRole.IdTeam ? "provisioning" : "role assignment")} is switched off: your team does this task.");
        if (task.State != TeamTaskState.Open || task.Agent?.Manual is not { } reason)
            throw AppException.Conflict("not_left_by_agent", $"The AI agent did not leave this {Messages.Team(team)} task to your team, so there is nothing to hand back.");
        var now = clock.UtcNow;
        var back = task with { PickedUpAt = null, PickedUpBy = null, Agent = task.Agent with { Manual = null, RefusedSince = null } };
        var next = (team == AppRole.IdTeam ? flow with { IdTeamTask = back } : flow with { RoleTeamTask = back })
            .Waiting(WaitingOn.Agent, now)
            .Log(now, caller.UserId, $"{Route(team)}-handed-back",
                $"{Messages.Team(team)} handed the task back to the AI agent ({caller.DisplayName}); it runs every check again on its next run. It had left it because: {reason}.");
        await SaveAsync(current, current.Value with { Flow = next, UpdatedAt = now }, ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    public async Task<RequestView> ReportIdTeamAsync(CallerContext caller, string projectId, string requestId, IdTeamReportInput input, string cid, CancellationToken ct)
    {
        if (!caller.InProject(projectId)) throw AppException.NotFound("Project");
        var current = await requests.LoadAsync(projectId, requestId, ct);
        RequireFor(caller, current.Value, Permission.WorkIdTeamTasks);
        var (flow, task) = OpenTask(current.Value, AppRole.IdTeam);
        await RequireProdApprovedAsync(current.Value, ct);
        var now = clock.UtcNow;

        // v2.1: when the AI agent created the user, group and membership, those facts come from the task, not the form.
        var byAgent = task.Agent is { Completed: true, Manual: null, Membership: true } a ? a : null;
        var upn = byAgent is not null ? current.Value.DomainId!
            : RequestService.Text(input.UserPrincipalName, "userPrincipalName", 200) ?? throw AppException.Invalid("userPrincipalName is required.");
        if (!SignIn().IsMatch(upn)) throw AppException.Invalid("userPrincipalName must be a sign-in name.");
        var group = byAgent is not null ? flow.Scope!.EntraGroup
            : RequestService.Text(input.GroupName, "groupName", 256) ?? throw AppException.Invalid("groupName is required.");
        var added = byAgent is not null || (input.AddedToGroup ?? false);
        TapRecord? tap = null;
        if (input.Tap is { } t)
        {
            // TAP rules: after all approvals and group membership; one-time; shortest lifetime. Metadata only.
            var issued = t.IssuedAt ?? throw AppException.Invalid("tap.issuedAt is required.");
            var expires = t.ExpiresAt ?? throw AppException.Invalid("tap.expiresAt is required.");
            if (issued > now.AddMinutes(5)) throw AppException.Invalid("tap.issuedAt is in the future.");
            if (expires <= issued) throw AppException.Invalid("tap.expiresAt must be after tap.issuedAt.");
            if (t.OneTime != true) throw AppException.Invalid("The Temporary Access Pass must be one-time use.");
            if ((expires - issued).TotalMinutes > settings.TapMaxLifetimeMinutes)
                throw AppException.Invalid($"Use the shortest lifetime: at most {settings.TapMaxLifetimeMinutes} minutes.");
            var security = flow.CurrentApproval(ApprovalStage.Security);
            if (security is null || issued < Verifier.Minute(security.At)) throw AppException.Invalid("The Temporary Access Pass was issued before all approvals were complete.");
            if (!added) throw AppException.Invalid("Issue the Temporary Access Pass only after the user is added to the group.");
            var by = SecretMasker.Apply(RequestService.Text(t.IssuedBy, "tap.issuedBy", 200)) ?? caller.DisplayName;
            tap = new TapRecord(issued, expires, true, by, now);
        }
        if ((byAgent?.UserCreated ?? input.UserCreated) == true && tap is null)
            throw AppException.Invalid("A new account needs a one-time Temporary Access Pass for the first sign-in. Record its metadata (issued, expiry, one-time, issued by) — never the pass.");

        var report = new IdTeamReport
        {
            UserPrincipalName = upn.ToLowerInvariant(),
            UserObjectId = byAgent?.UserObjectId ?? Guid(input.UserObjectId, "userObjectId"),
            GroupName = group,
            GroupObjectId = byAgent?.GroupObjectId ?? Guid(input.GroupObjectId, "groupObjectId"),
            UserCreated = byAgent?.UserCreated ?? input.UserCreated ?? false,
            GroupCreated = byAgent?.GroupCreated ?? input.GroupCreated ?? false,
            AddedToGroup = added,
            MappingSheetUpdated = byAgent is not null ? byAgent.MappingUpdated || flow.Scope!.GroupState == GroupState.Exists : input.MappingSheetUpdated ?? false,
            Tap = tap,
            Notes = SecretMasker.Apply(RequestService.Text(input.Notes, "notes", 1000)),
        };
        var reported = task with
        {
            State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = caller.DisplayName, IdReport = report,
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? caller.DisplayName,
        };
        var next = (flow with { IdTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, caller.UserId, "id-team-reported", tap is null ? "ID team recorded completion." : "ID team recorded completion and TAP metadata (issued, expiry, one-time, issued-by).");
        await SaveAsync(current, current.Value with { Flow = next, UpdatedAt = now }, ct);
        await AfterSavedAsync(projectId, requestId, "ID team report", c => agent.AdvanceAsync(projectId, requestId, cid, c), ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    public async Task<RequestView> ReportRoleTeamAsync(CallerContext caller, string projectId, string requestId, RoleTeamReportInput input, string cid, CancellationToken ct)
    {
        if (!caller.InProject(projectId)) throw AppException.NotFound("Project");
        var current = await requests.LoadAsync(projectId, requestId, ct);
        RequireFor(caller, current.Value, Permission.WorkRoleTeamTasks);
        var (flow, task) = OpenTask(current.Value, AppRole.RoleTeam);
        await RequireProdApprovedAsync(current.Value, ct);
        var roles = (input.AssignedRoles ?? [])
            .Select(a => new AssignedRole(
                RequestService.Text(a.Role, "role", 200) ?? throw AppException.Invalid("Each assigned role needs a role name."),
                RequestService.Text(a.Scope, "scope", 500)))
            .ToList();
        if (roles.Count == 0) throw AppException.Invalid("assignedRoles is required: list exactly what you assigned.");
        var now = clock.UtcNow;

        // The role assignment team updates the response document (through this form).
        await UpdateResponseRecordAsync(projectId, requestId, caller.UserId, x => x with
        {
            AssignedRoles = roles, State = ResponseState.Assigned, AssignedBy = caller.DisplayName, AssignedAt = now,
        }, ct);

        var reported = task with
        {
            State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = caller.DisplayName,
            RoleReport = new RoleTeamReport { AssignedRoles = roles, Notes = SecretMasker.Apply(RequestService.Text(input.Notes, "notes", 1000)) },
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? caller.DisplayName,
        };
        var next = (flow with { RoleTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, caller.UserId, "role-team-reported", "Role assignment team recorded the assignment and updated the response document.");
        await SaveAsync(current, current.Value with { Flow = next, UpdatedAt = now }, ct);
        await AfterSavedAsync(projectId, requestId, "role team report", c => agent.AdvanceAsync(projectId, requestId, cid, c), ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    /// <summary>Operator: run the agent step for one request now (e.g. without the background worker).</summary>
    public async Task<RequestView> AdvanceAsync(CallerContext caller, string projectId, string requestId, string cid, CancellationToken ct)
    {
        caller.Require(projectId, Permission.RecoverRuns);
        await requests.LoadAsync(projectId, requestId, ct);
        await agent.AdvanceAsync(projectId, requestId, cid, ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    /// <summary>
    /// Operator, after a developer fixed the cause of a Failed-Needs-Dev: back to the stage that failed (a failed review
    /// starts a new review). The agent re-runs that stage's checks; approvals and team reports already given stay, nothing is skipped.
    /// </summary>
    public async Task<RequestView> ResumeAsync(CallerContext caller, string projectId, string requestId, string cid, CancellationToken ct)
    {
        caller.Require(projectId, Permission.RecoverRuns);
        var current = await requests.LoadAsync(projectId, requestId, ct);
        var r = current.Value;
        if (r.Flow is not { Status: RequestStatus.FailedNeedsDev } flow)
            throw AppException.Conflict("not_failed", "Only a request in Failed-Needs-Dev can be resumed.");
        var failedIn = flow.Stages.Count >= 2 ? flow.Stages[^2] : null;
        var (status, waiting) = failedIn is null || failedIn.Status is RequestStatus.Submitted or RequestStatus.AiReview
            ? (RequestStatus.Submitted, WaitingOn.Agent)
            : (failedIn.Status, failedIn.WaitingOn);
        var now = clock.UtcNow;
        var next = (flow with { Attempts = new Dictionary<string, int>(), LastReason = null, ReviewRunId = status == RequestStatus.Submitted ? null : flow.ReviewRunId })
            .To(status, waiting, now, caller.UserId, "resume",
                $"Resumed by {caller.DisplayName} after a developer fix: back to {StatusText.Label(status)}; the agent re-runs this stage's checks.");
        await SaveAsync(current, r with { Flow = next, UpdatedAt = now }, ct);
        logger.LogInformation("resume request={RequestId} to={Status} by={UserId} cid={Cid}", requestId, status, caller.UserId, cid);
        await AfterSavedAsync(projectId, requestId, "resume", async c =>
        {
            await issues.ResolveAsync(projectId, requestId, [IssueKind.NeedsDeveloper], c);
            await agent.AdvanceAsync(projectId, requestId, cid, c);
        }, ct);
        return await ViewAsync(caller, projectId, requestId, ct);
    }

    // ---- Views --------------------------------------------------------------------------------------------------

    public async Task<RequestView> ViewAsync(CallerContext caller, string projectId, string requestId, CancellationToken ct)
    {
        var current = await requests.GetAsync(caller, projectId, requestId, ct);
        var r = current.Value;
        var now = clock.UtcNow;
        var issueList = caller.Has(projectId, Permission.ViewIssues)
            ? (await stores.Issues.ListAsync(projectId, ct)).Select(i => i.Value).Where(i => i.RequestId == requestId).OrderByDescending(i => i.LastSeenAt).ToList()
            : [];
        var actions = ActionsFor(caller, r);
        var prod = r.Flow is { Status: RequestStatus.AwaitingProdApproval, Scope: { } scope }
            ? (await stores.Escalations.GetAsync(projectId, Resources.ProdGuard.EscalationId(r, scope), ct))?.Value : null;
        return new RequestView(Endpoints.ToItem(current), Reports.StatusLine(r, now), Reports.Timings(r, now), actions,
            await notifier.ForRequestAsync(projectId, requestId, ct), issueList, StatusText.FlowOverview, Messages.WhoDoesWhatFor(settings))
        {
            ApprovalHint = ApprovalHint(caller, r, actions),
            ProdEscalation = prod is { Status: EscalationStatus.Open } ? prod : null,
        };
    }

    /// <summary>
    /// Shown instead of the decision panel when the request waits for an approval the caller cannot give, so a missing
    /// Owner decision is never a silent blank: who it waits for, and why this sign-in does not count.
    /// </summary>
    public static string? ApprovalHint(CallerContext caller, AccessRequest r, IReadOnlyList<string> actions)
    {
        var f = r.Flow;
        if (f is null || actions.Contains(Actions.ApproveOwner) || actions.Contains(Actions.ApproveSecurity)) return null;
        var p = r.ProjectId;
        var me = string.IsNullOrWhiteSpace(caller.DisplayName) ? "this account" : caller.DisplayName;
        if (f.Status == RequestStatus.AwaitingOwner)
        {
            if (caller.Is(r.Owner) && !caller.Has(p, Permission.ApproveAsOwner))
                return $"You are the Owner named on this request, but {me} has no Owner role in this project yet, so the decision is not shown. " +
                       "Ask the pilot operator to register your account with the Owner role (tools/seed_sample_data.py --register-user … --roles Owner), then refresh.";
            if (caller.Has(p, Permission.ApproveAsOwner))
                return $"Waiting for the Owner named on the request: {r.Owner}. You are signed in as {me}; only the named Owner can decide. " +
                       "If that is you under another sign-in name, sign in with that account or ask the requester to correct the Owner.";
            return $"Waiting for the Owner named on the request: {r.Owner}.";
        }
        if (f.Status == RequestStatus.AwaitingSecurity)
        {
            if (caller.Has(p, Permission.ApproveAsSecurity) && f.CurrentApproval(ApprovalStage.Owner)?.By == caller.UserId)
                return "You gave the Owner approval on this request, so another Security approver must decide (four eyes).";
            return "Waiting for a Security approver.";
        }
        if (f.Status == RequestStatus.AwaitingProdApproval && !actions.Contains(Actions.DecideProd))
            return caller.Has(p, Permission.DecideProdEscalation)
                ? "You submitted, approved or get the access of this request, so another ProdApprover must decide the Prod stage (four eyes)."
                : "Waiting for a ProdApprover (not the requester, the Owner or the Security approver). They decide here or on the Escalations page.";
        return null;
    }

    public async Task<IReadOnlyList<StatusLine>> ListAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return (await requests.ListAsync(caller, projectId, ct)).Select(r => Reports.StatusLine(r.Value, now)).ToList();
    }

    /// <summary>Everything waiting on the caller across their projects.</summary>
    public async Task<IReadOnlyList<ActionItem>> MyActionsAsync(CallerContext caller, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var items = new List<ActionItem>();
        foreach (var projectId in caller.ProjectsWith(Permission.ViewRequests))
            foreach (var r in await stores.Requests.ListAsync(projectId, ct))
                foreach (var action in ActionsFor(caller, r.Value).Where(a => a != Actions.Delete)) // deleting is never "waiting on you"
                    items.Add(new ActionItem(Reports.StatusLine(r.Value, now), action));
        return items.OrderBy(i => i.Status.UpdatedAt).ToList();
    }

    public async Task<string> TrailAsync(CallerContext caller, string projectId, string requestId, CancellationToken ct)
    {
        var r = (await requests.GetAsync(caller, projectId, requestId, ct)).Value;
        var runs = (await stores.Runs.ListAsync(projectId, ct)).Select(x => x.Value).Where(x => x.RequestId == requestId).ToList();
        var notes = await notifier.ForRequestAsync(projectId, requestId, ct);
        var issueList = caller.Has(projectId, Permission.ViewIssues)
            ? (await stores.Issues.ListAsync(projectId, ct)).Select(i => i.Value).Where(i => i.RequestId == requestId).ToList()
            : [];
        return Reports.Trail(r, runs, notes, issueList, clock.UtcNow);
    }

    public async Task<IReadOnlyList<Issue>> IssuesAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewIssues);
        return (await stores.Issues.ListAsync(projectId, ct)).Select(i => i.Value).OrderByDescending(i => i.LastSeenAt).ToList();
    }

    public async Task<DailySummary> DailyAsync(CallerContext caller, string projectId, DateOnly date, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewReports);
        var all = (await stores.Requests.ListAsync(projectId, ct)).Select(r => r.Value).ToList();
        var issueList = (await stores.Issues.ListAsync(projectId, ct)).Select(i => i.Value).ToList();
        return Reports.Daily(projectId, date, all, issueList, clock.UtcNow);
    }

    public async Task<DeveloperReport> DeveloperAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewReports);
        var all = (await stores.Requests.ListAsync(projectId, ct)).Select(r => r.Value).ToList();
        var issueList = (await stores.Issues.ListAsync(projectId, ct)).Select(i => i.Value).ToList();
        return Reports.Developer(projectId, all, issueList, clock.UtcNow);
    }

    public static IReadOnlyList<string> ActionsFor(CallerContext caller, AccessRequest r)
    {
        var f = r.Flow;
        if (f is null) return [];
        var p = r.ProjectId;
        var list = new List<string>();
        // A simulation: the developer acts for every stage (Simulate). Never on a real request.
        var sim = r.Simulated && caller.Has(p, Permission.Simulate);
        if (f.Status == RequestStatus.AwaitingOwner && (sim || (caller.Has(p, Permission.ApproveAsOwner) && caller.Is(r.Owner)))) list.Add(Actions.ApproveOwner);
        if (f.Status == RequestStatus.AwaitingSecurity && (sim || (caller.Has(p, Permission.ApproveAsSecurity) && f.CurrentApproval(ApprovalStage.Owner)?.By != caller.UserId)))
            list.Add(Actions.ApproveSecurity);
        if (f.Status == RequestStatus.WithIdTeam && f.WaitingOn == WaitingOn.IdTeam && f.IdTeamTask?.State is TeamTaskState.Open or TeamTaskState.Returned && Can(caller, r, Permission.WorkIdTeamTasks))
            list.Add(Actions.IdTeamTask);
        if (f.Status == RequestStatus.WithRoleTeam && f.WaitingOn == WaitingOn.RoleTeam && f.RoleTeamTask?.State is TeamTaskState.Open or TeamTaskState.Returned && Can(caller, r, Permission.WorkRoleTeamTasks))
            list.Add(Actions.RoleTeamTask);
        // Same four-eyes rules as DecideProdEscalationAsync (which checks them again): never the requester, the person who gets
        // the access or an earlier approver of a real request.
        if (f.Status == RequestStatus.AwaitingProdApproval && (sim || (caller.Has(p, Permission.DecideProdEscalation) && !SubmittedBy(r, caller)
                && !caller.Is(r.EnterpriseId) && !caller.Is(r.DomainId)
                && f.CurrentApproval(ApprovalStage.Owner)?.By != caller.UserId && f.CurrentApproval(ApprovalStage.Security)?.By != caller.UserId)))
            list.Add(Actions.DecideProd);
        if (f.Status == RequestStatus.Returned && Can(caller, r, Permission.SubmitRequests)) list.Add(Actions.Resubmit);
        if (caller.Has(p, Permission.DeleteRequests)) list.Add(Actions.Delete);
        return list;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static (WorkflowState Flow, TeamTask Task) OpenTask(AccessRequest r, AppRole team)
    {
        var flow = r.Flow ?? throw AppException.Conflict("legacy_request", "This is a v1 request; it has no team tasks.");
        var expected = team == AppRole.IdTeam ? RequestStatus.WithIdTeam : RequestStatus.WithRoleTeam;
        var task = team == AppRole.IdTeam ? flow.IdTeamTask : flow.RoleTeamTask;
        if (flow.Status != expected || task is null)
            throw AppException.Conflict("no_open_task", $"The request is {StatusText.Label(flow.Status)}; there is no open {Messages.Team(team)} task.");
        if (task.State is not (TeamTaskState.Open or TeamTaskState.Returned))
            throw AppException.Conflict("task_not_open", $"The {Messages.Team(team)} task is {task.State}{(task.State == TeamTaskState.Reported ? "; the AI agent is verifying it" : "")}.");
        return (flow, task);
    }

    private static string Route(AppRole team) => team == AppRole.IdTeam ? "id-team" : "role-team";

    private async Task SaveAsync(Versioned<AccessRequest> current, AccessRequest next, CancellationToken ct)
    {
        try
        {
            await stores.Requests.ReplaceAsync(next, current.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Stale();
        }
    }

    private async Task UpdateResponseRecordAsync(string projectId, string requestId, string userId, Func<ResponseRecord, ResponseRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await stores.ResponseDocument.GetAsync(projectId, requestId, ct);
            if (existing is null)
            {
                logger.LogWarning("response record missing request={RequestId}", requestId);
                return;
            }
            try
            {
                await stores.ResponseDocument.ReplaceAsync(change(existing.Value) with
                {
                    UpdatedBy = userId, UpdatedAt = clock.UtcNow, Version = existing.Value.Version + 1,
                }, existing.ETag, ct);
                return;
            }
            catch (ConcurrencyException)
            {
                // Changed meanwhile; re-read and apply again.
            }
        }
        throw AppException.Stale();
    }

    private static string? Guid(string? value, string field)
    {
        var v = RequestService.Text(value, field, 64);
        if (v is null) return null;
        return System.Guid.TryParse(v, out var g) ? g.ToString("D") : throw AppException.Invalid($"{field} must be an Entra object id (GUID).");
    }

    [GeneratedRegex("^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")]
    private static partial Regex SignIn();
}
