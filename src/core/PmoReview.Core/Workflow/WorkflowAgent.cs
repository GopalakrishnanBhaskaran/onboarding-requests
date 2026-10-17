using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Processing;
using PmoReview.Core.Review;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <summary>
/// The AI agent's side of the flow: reviews, validates, summarises, routes, tracks, verifies and reports. It never
/// approves, creates or assigns anything. <see cref="AdvanceAsync"/> is idempotent: it looks at where a request stands
/// and does the next agent step, if any. The API calls it after each human action; the worker calls it for every open
/// request on a timer (and is the only place with the read-only Entra reader in the pilot).
/// </summary>
public sealed partial class WorkflowAgent(
    DataStores stores,
    ISourcesOfTruth sources,
    RunService runs,
    Notifier notifier,
    IssueLog issues,
    Verifier verifier,
    WorkflowSettings settings,
    ReviewPolicy policy,
    IClock clock,
    ILogger<WorkflowAgent> logger,
    bool canCrossCheck = false,
    IDirectoryReader? directory = null,
    IAiAssistant? assistant = null,
    IDirectoryWriter? writer = null,
    IRoleAssignmentWriter? roleWriter = null)
{
    private const string Agent = WorkflowState.AgentActor;
    private const int MaxStepsPerCall = 8;

    /// <param name="Stop">End this call after saving (a retry waits for the next agent run instead of looping now).</param>
    private sealed record Step(AccessRequest Next, List<Func<CancellationToken, Task>> Effects, bool Stop = false);

    /// <summary>Runs agent steps for one request until nothing more can be done now.</summary>
    public async Task<AccessRequest?> AdvanceAsync(string projectId, string requestId, string correlationId, CancellationToken ct)
    {
        AccessRequest? last = null;
        for (var i = 0; i < MaxStepsPerCall; i++)
        {
            var current = await stores.Requests.GetAsync(projectId, requestId, ct);
            if (current?.Value.Flow is null || current.Value.ProjectId != projectId) return current?.Value;
            last = current.Value;
            Step? step;
            try
            {
                step = await NextAsync(current.Value, correlationId, ct);
            }
            catch (Exception e) when (e is DirectoryReadException or SourceUnavailableException)
            {
                step = await DependencyFailedAsync(current.Value, e is DirectoryReadException d ? d.Category : "SourceUnavailable", ct);
            }
            if (step is null) return current.Value;
            try
            {
                var saved = await stores.Requests.ReplaceAsync(step.Next with { UpdatedAt = clock.UtcNow }, current.ETag, ct);
                last = saved.Value;
            }
            catch (ConcurrencyException)
            {
                continue; // a person acted meanwhile; re-read and decide again
            }
            foreach (var effect in step.Effects) await effect(ct);
            if (step.Stop) return last;
        }
        return last;
    }

    /// <summary>Worker: every open request of every project, plus stuck-run recovery.</summary>
    public async Task<int> RunOnceAsync(string correlationId, CancellationToken ct)
    {
        var advanced = 0;
        foreach (var project in await stores.Projects.ListAllAsync(ct))
        {
            try
            {
                await runs.RecoverStuckAsync(project.Value.ProjectId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await RecordErrorAsync(project.Value.ProjectId, null, "stuck-run recovery", e, ct); // the requests still get their turn
            }
            foreach (var r in await stores.Requests.ListAsync(project.Value.ProjectId, ct))
            {
                if (r.Value.Flow is not { IsOpen: true }) continue;
                try
                {
                    var before = r.Value.Flow.Events.Count;
                    var after = await AdvanceAsync(r.Value.ProjectId, r.Value.Id, correlationId, ct);
                    if (after?.Flow?.Events.Count > before) advanced++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await RecordErrorAsync(r.Value, e, ct);
                }
            }
        }
        return advanced;
    }

    /// <summary>
    /// An error the agent cannot classify. Never silent: logged and put on the issues list (Issues and reports) with the
    /// error type, counted per request + stage + type. The request is unchanged and the next agent run retries it.
    /// </summary>
    public Task RecordErrorAsync(AccessRequest r, Exception e, CancellationToken ct) =>
        RecordErrorAsync(r.ProjectId, r.Id, StatusText.Label(r.Flow?.Status ?? RequestStatus.Submitted), e, ct);

    private async Task RecordErrorAsync(string projectId, string? requestId, string where, Exception e, CancellationToken ct)
    {
        var type = e.GetType().Name;
        logger.LogError("agent: {Where} failed {ExceptionType} request={RequestId}", where, type, requestId);
        try
        {
            var message = e.Message.Length > 300 ? e.Message[..300] + "…" : e.Message;
            var what = requestId is null ? $"{where} for this project" : $"this request at {where}";
            await issues.RecordAsync(projectId, requestId, $"agent-error:{where}:{type}", IssueKind.NeedsDeveloper, IssueSeverity.High,
                $"The AI agent could not process {what} ({type}). Nothing was changed and every agent run retries; a developer is needed while this recurs (see last seen).",
                $"{e.GetType().FullName}: {message}", ct);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            logger.LogError("agent: could not record the error ({ExceptionType})", inner.GetType().Name);
        }
    }

    private async Task<Step?> NextAsync(AccessRequest r, string cid, CancellationToken ct)
    {
        var flow = r.Flow!;
        return flow.Status switch
        {
            RequestStatus.Submitted => await StartReviewAsync(r, flow.Events.LastOrDefault()?.Step ?? "submit", cid, ct),
            RequestStatus.AiReview => await AfterReviewAsync(r, cid, ct),
            RequestStatus.AwaitingOwner or RequestStatus.AwaitingSecurity => await MappingChangedAsync(r, ct) ?? await ApprovalSlaAsync(r, ct),
            RequestStatus.WithIdTeam => await MappingChangedAsync(r, ct) ?? await IdTeamAsync(r, ct),
            RequestStatus.WithRoleTeam => await MappingChangedAsync(r, ct) ?? await RoleTeamAsync(r, ct),
            RequestStatus.Verifying => await FinalAsync(r, ct),
            _ => null,
        };
    }

    // ---- Stage 2: AI review -------------------------------------------------------------------------------------

    private async Task<Step?> StartReviewAsync(AccessRequest r, string trigger, string cid, CancellationToken ct)
    {
        var flow = r.Flow!;
        var effects = new List<Func<CancellationToken, Task>>();
        if (flow.Status == RequestStatus.Submitted && r.Revision == 1 && trigger == "submit")
        {
            var (subject, body) = Messages.FlowOverview(r, settings);
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"submit:r{r.Revision}", NotificationKind.FlowOverview, Recipients.Requester(r), subject, body, c));
        }

        // Reuse a run already started for this stage (e.g. a save that lost a race after the run was created).
        var latest = await runs.LatestAsync(r.ProjectId, r.Id, ct);
        string runId;
        if (latest is not null && latest.RequestRevision == r.Revision && latest.QueuedAt >= flow.StageSince && latest.Trigger == trigger
            && latest.ProcessingStatus != ProcessingStatus.Failed)
        {
            runId = latest.Id;
        }
        else
        {
            var started = await runs.StartAsync(r, trigger, Agent, cid);
            runId = started.RunId;
        }
        var next = flow.Status == RequestStatus.AiReview
            ? flow.Log(clock.UtcNow, Agent, "review-start", $"AI review started ({trigger}).")
            : flow.To(RequestStatus.AiReview, WaitingOn.Agent, clock.UtcNow, Agent, "review-start", $"AI review started ({trigger}).");
        return new Step(r with { Flow = next with { ReviewRunId = runId } }, effects);
    }

    private async Task<Step?> AfterReviewAsync(AccessRequest r, string cid, CancellationToken ct)
    {
        var flow = r.Flow!;
        if (flow.ReviewRunId is null)
            return await StartReviewAsync(r, flow.ResumeStatus is null ? "review" : "mapping-changed", cid, ct);
        var run = (await stores.Runs.GetAsync(r.ProjectId, flow.ReviewRunId, ct))?.Value;
        if (run is null || run.RequestRevision != r.Revision || run.ProcessingStatus == ProcessingStatus.Superseded)
            return await StartReviewAsync(r, "review", cid, ct);

        switch (run.ProcessingStatus)
        {
            case ProcessingStatus.Queued or ProcessingStatus.Running:
                return null;
            case ProcessingStatus.Failed:
                return await RetryOrFailAsync(r, $"review:r{r.Revision}", $"The AI review could not complete ({run.ErrorCategory}).",
                    () => StartReviewAsync(r, "auto-retry", cid, ct), ct);
        }

        var result = run.Result!;
        switch (result.Outcome)
        {
            case ReviewOutcome.Pending:
                // The read-only Entra cross-check has not run. Only a process with the Entra reader can finish it.
                if (!canCrossCheck) return null;
                if (run.Trigger != "entra-cross-check") return await StartReviewAsync(r, "entra-cross-check", cid, ct);
                return await RetryOrFailAsync(r, $"crosscheck:r{r.Revision}", "The read-only Entra cross-check could not complete.",
                    () => StartReviewAsync(r, "entra-cross-check-retry", cid, ct), ct);
            case ReviewOutcome.Fail:
                return await ReturnAsync(r, run, ct);
            default:
                return await PassedAsync(r, run, ct);
        }
    }

    private async Task<Step> ReturnAsync(AccessRequest r, ReviewRun run, CancellationToken ct)
    {
        var project = await ProjectAsync(r, ct);
        var reasons = run.Result!.ReturnReasons;
        var now = clock.UtcNow;
        var flow = r.Flow! with
        {
            Approvals = r.Flow!.Approvals.Select(a => a with { Superseded = true }).ToList(),
            IdTeamTask = Cancel(r.Flow.IdTeamTask),
            RoleTeamTask = Cancel(r.Flow.RoleTeamTask),
            ResumeStatus = null,
            LastReason = SecretMasker.Apply(string.Join(" | ", reasons)),
        };
        flow = flow.To(RequestStatus.Returned, WaitingOn.Requester, now, Agent, "review-fail",
            $"AI review failed ({reasons.Count} reason(s)); returned to the requester and PMO. Not sent for approval.");
        var (subject, body) = Messages.Returned(r, reasons, settings);
        return new Step(r with { Flow = flow },
        [
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"returned:{run.Id}", NotificationKind.ReviewReturned,
                Recipients.Requester(r).Concat(Recipients.Pmo(project)), subject, body, c),
        ]);
    }

    private async Task<Step> PassedAsync(AccessRequest r, ReviewRun run, CancellationToken ct)
    {
        var project = await ProjectAsync(r, ct);
        var flow = r.Flow!;
        var result = run.Result!;
        var m = result.Mapping;
        // The Azure role on the reviewed row is part of what is approved (a later change re-reviews: fingerprint).
        var reviewedRow = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == m.RowId);
        var scope = new ReviewedScope
        {
            ReviewRunId = run.Id,
            MappingRowId = m.RowId!,
            MappingRowVersion = m.RowVersion ?? 1,
            EntraGroup = m.EntraGroup!,
            Role = m.Role!,
            RoleScope = m.RoleScope,
            AzureRoleDefinitionId = reviewedRow?.AzureRoleDefinitionId,
            AzureScope = reviewedRow?.AzureScope,
            GroupState = m.GroupState ?? GroupState.Exists,
            GroupExistsInEntra = m.GroupExistsInEntra,
            SheetFingerprint = run.SheetFingerprint!,
            RiskFlags = result.RiskFlags,
            Summary = result.Summary,
        };
        var now = clock.UtcNow;
        var effects = new List<Func<CancellationToken, Task>>();
        await UpsertResponseRecordAsync(r, scope, ct);
        if (scope.GroupExistsInEntra == true && scope.GroupState == GroupState.ToBeCreated)
            effects.Add(c => issues.RecordAsync(r.ProjectId, r.Id, $"review:{run.Id}", IssueKind.AutoFixApplied, IssueSeverity.Info,
                $"Group {scope.EntraGroup} already exists in Entra; the ID team task says to reuse it.", null, c, IssueStatus.AutoFixed));

        // A mapping change sent an in-flight request back here: continue where it was if the access is the same.
        if (flow.ResumeStatus is { } resume && flow.Scope is { } previous && previous.SameAccess(scope))
        {
            var resumed = (flow with { Scope = scope, ResumeStatus = null })
                .To(resume, WaitingFor(resume, flow), now, Agent, "review-pass", "Mapping sheet changed; the new review passed with the same group and role. Continuing.");
            effects.Add(c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.MappingChangedAfterReview], c));
            if (resume == RequestStatus.AwaitingSecurity) effects.Add(c => AskApprovalAsync(r, ApprovalStage.Security, scope, resumed, project, c));
            if (resume == RequestStatus.AwaitingOwner) effects.Add(c => AskApprovalAsync(r, ApprovalStage.Owner, scope, resumed, project, c));
            return new Step(r with { Flow = resumed }, effects);
        }

        var changed = flow.Scope is not null && !flow.Scope.SameAccess(scope);
        var next = flow with
        {
            Scope = scope,
            ResumeStatus = null,
            // Approvals and tasks refer to the old group/role: they no longer count.
            Approvals = changed ? flow.Approvals.Select(a => a with { Superseded = true }).ToList() : flow.Approvals,
            IdTeamTask = changed ? Cancel(flow.IdTeamTask) : flow.IdTeamTask,
            RoleTeamTask = changed ? Cancel(flow.RoleTeamTask) : flow.RoleTeamTask,
        };
        next = next.To(RequestStatus.AwaitingOwner, WaitingOn.Owner, now, Agent, "review-pass",
            changed ? "New review passed with a different group or role; earlier approvals no longer count. Sent to the Owner."
                    : "AI review passed. Sent to the named Owner with the AI summary.");
        effects.Add(c => AskApprovalAsync(r, ApprovalStage.Owner, scope, next, project, c));
        return new Step(r with { Flow = next }, effects);
    }

    private WaitingOn WaitingFor(RequestStatus s, WorkflowState flow) => s switch
    {
        RequestStatus.AwaitingOwner => WaitingOn.Owner,
        RequestStatus.AwaitingSecurity => WaitingOn.Security,
        RequestStatus.WithIdTeam => flow.IdTeamTask is { } t && (t.State == TeamTaskState.Reported || ProvisioningPending(t)) ? WaitingOn.Agent : WaitingOn.IdTeam,
        RequestStatus.WithRoleTeam => flow.RoleTeamTask is { } t && (t.State == TeamTaskState.Reported || RoleAssignmentPending(t)) ? WaitingOn.Agent : WaitingOn.RoleTeam,
        RequestStatus.Verifying => WaitingOn.User,
        _ => WaitingOn.Agent,
    };

    public Task AskApprovalAsync(AccessRequest r, ApprovalStage stage, ReviewedScope scope, WorkflowState flow, Project? project, CancellationToken ct)
    {
        var (subject, body) = Messages.ApprovalRequested(r, stage, scope, stage == ApprovalStage.Security ? flow.CurrentApproval(ApprovalStage.Owner) : null, settings);
        var to = stage == ApprovalStage.Owner ? Recipients.Owner(r) : Recipients.Security(project);
        return notifier.EnqueueAsync(r.ProjectId, r.Id, $"{stage.ToString().ToLowerInvariant()}-approval:{scope.ReviewRunId}", NotificationKind.ApprovalRequested, to, subject, body, ct);
    }

    /// <summary>Writes (or updates) this request's record in the response document from the passing review.</summary>
    private async Task UpsertResponseRecordAsync(AccessRequest r, ReviewedScope scope, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var existing = await stores.ResponseDocument.GetAsync(r.ProjectId, r.Id, ct);
        var row = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == scope.MappingRowId);
        if (existing is null)
        {
            try
            {
                await stores.ResponseDocument.CreateAsync(new ResponseRecord
                {
                    Id = r.Id, ProjectId = r.ProjectId, RequestId = r.Id, UserId = r.DomainId!, Resource = r.Resource!, AccessLevel = r.AccessLevel!,
                    EntraGroup = scope.EntraGroup, Role = scope.Role, RoleScope = scope.RoleScope, Privileged = row?.Privileged ?? false,
                    State = ResponseState.PendingApproval, Justification = SecretMasker.Apply(r.Justification)!,
                    UpdatedBy = Agent, UpdatedAt = now, Version = 1, IsSample = row?.IsSample ?? false,
                }, ct);
            }
            catch (DocumentConflictException)
            {
                // Written concurrently by another agent run.
            }
            return;
        }
        var e = existing.Value;
        var same = MappingSheet.Same(e.EntraGroup, scope.EntraGroup) && MappingSheet.Same(e.Role, scope.Role)
                   && string.Equals(e.RoleScope ?? "", scope.RoleScope ?? "", StringComparison.OrdinalIgnoreCase)
                   && MappingSheet.Same(e.UserId, r.DomainId) && e.State != ResponseState.Rejected;
        if (same) return;
        try
        {
            await stores.ResponseDocument.ReplaceAsync(e with
            {
                UserId = r.DomainId!, Resource = r.Resource!, AccessLevel = r.AccessLevel!, EntraGroup = scope.EntraGroup, Role = scope.Role,
                RoleScope = scope.RoleScope, Privileged = row?.Privileged ?? e.Privileged, State = ResponseState.PendingApproval,
                Justification = SecretMasker.Apply(r.Justification)!, OwnerApprovedBy = null, OwnerApprovedAt = null,
                SecurityApprovedBy = null, SecurityApprovedAt = null, AssignedRoles = [], AssignedBy = null, AssignedAt = null,
                UpdatedBy = Agent, UpdatedAt = now, Version = e.Version + 1,
            }, existing.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            // Changed meanwhile; the next agent pass re-checks it.
        }
    }

    // ---- Gates: mapping change, approval SLA -------------------------------------------------------------------

    /// <summary>"Mapping sheet changed after AI review": re-run the review before continuing.</summary>
    private async Task<Step?> MappingChangedAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        if (flow.Scope is null) return null;
        var sheet = await sources.ReadMappingSheetAsync(r.ProjectId, ct);
        if (MappingSheet.Fingerprint(sheet, r.Resource) == flow.Scope.SheetFingerprint) return null;
        var from = flow.Status;
        var next = (flow with { ResumeStatus = from, ReviewRunId = null })
            .To(RequestStatus.AiReview, WaitingOn.Agent, clock.UtcNow, Agent, "mapping-changed",
                $"Mapping sheet rows for '{r.Resource}' changed after the AI review; re-running the review before continuing ({StatusText.Label(from)}).");
        return new Step(r with { Flow = next },
        [
            c => issues.RecordAsync(r.ProjectId, r.Id, $"mapping-changed:{flow.Scope.ReviewRunId}", IssueKind.MappingChangedAfterReview, IssueSeverity.Info,
                $"Mapping sheet changed after the AI review (at {StatusText.Label(from)}); review re-run.", null, c),
        ]);
    }

    private async Task<Step?> ApprovalSlaAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        var stage = flow.Status == RequestStatus.AwaitingOwner ? ApprovalStage.Owner : ApprovalStage.Security;
        var waiting = stage == ApprovalStage.Owner ? WaitingOn.Owner : WaitingOn.Security;
        return await SlaAsync(r, waiting, flow.StageSince, flow.RemindedAt, flow.EscalatedAt, $"{stage} approval",
            f => f with { RemindedAt = clock.UtcNow }, f => f with { EscalatedAt = clock.UtcNow },
            IssueKind.ApprovalSlaBreached, $"{stage.ToString().ToLowerInvariant()}-sla:{flow.Scope?.ReviewRunId}", ct);
    }

    /// <summary>Pending past SLA: one reminder, then escalation to the manager. Never skips or auto-approves.</summary>
    private async Task<Step?> SlaAsync(AccessRequest r, WaitingOn waiting, DateTimeOffset since, DateTimeOffset? reminded, DateTimeOffset? escalated,
        string what, Func<WorkflowState, WorkflowState> markReminded, Func<WorkflowState, WorkflowState> markEscalated, IssueKind kind, string step, CancellationToken ct)
    {
        var sla = settings.SlaFor(waiting);
        var pending = clock.UtcNow - since;
        if (pending <= sla) return null;
        var project = await ProjectAsync(r, ct);
        var flow = r.Flow!;
        if (reminded is null)
        {
            var (subject, body) = Messages.Reminder(r, what, pending, settings);
            var next = markReminded(flow).Log(clock.UtcNow, Agent, "reminder", $"{what} pending past SLA ({Messages.Hours(pending)}); reminder sent.");
            return new Step(r with { Flow = next },
            [
                c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{step}:reminder", NotificationKind.Reminder, Recipients.For(waiting, r, project), subject, body, c),
                c => issues.RecordAsync(r.ProjectId, r.Id, step, kind, IssueSeverity.Warning, $"{what} pending past SLA; reminder sent.", null, c),
            ]);
        }
        if (escalated is null && pending > sla * 2)
        {
            var manager = await ManagerAsync(waiting, r, project, ct);
            var (subject, body) = Messages.Escalation(r, what, waiting == WaitingOn.Owner ? r.Owner : StatusText.Label(waiting), pending, settings);
            var next = markEscalated(flow).Log(clock.UtcNow, Agent, "escalation",
                manager is null ? $"{what} still pending; no manager found, escalated to Operators." : $"{what} still pending; escalated to the manager.");
            return new Step(r with { Flow = next },
            [
                c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{step}:escalation", NotificationKind.Escalation,
                    manager is null ? Recipients.Operators(project) : [manager], subject, body, c),
                c => issues.RecordAsync(r.ProjectId, r.Id, step, kind, IssueSeverity.High, $"{what} pending past twice the SLA; escalated.", null, c),
            ]);
        }
        return null;
    }

    private async Task<string?> ManagerAsync(WaitingOn waiting, AccessRequest r, Project? project, CancellationToken ct)
    {
        var c = project?.Contacts;
        switch (waiting)
        {
            case WaitingOn.Owner:
                if (directory is not null && r.Owner is not null)
                {
                    try
                    {
                        var manager = await directory.GetManagerAsync(r.Owner, ct);
                        if (manager is not null) return manager;
                    }
                    catch (DirectoryReadException)
                    {
                        // Fall back to the configured contact.
                    }
                }
                return c?.OwnerManagerFallback;
            case WaitingOn.Security: return c?.SecurityManager;
            case WaitingOn.IdTeam: return c?.IdTeamManager;
            case WaitingOn.RoleTeam: return c?.RoleTeamManager;
            default: return null;
        }
    }

    // ---- Stage 4: ID team ---------------------------------------------------------------------------------------

    /// <summary>Creates the ID team task after Security approves. Used by <see cref="WorkflowService"/>; log step "id-team-task".</summary>
    public TeamTask NewIdTeamTask(AccessRequest r, WorkflowState flow) => new()
    {
        Id = Ids.Key(r.Id, $"id-team:{flow.Events.Count(e => e.Step == "id-team-task")}"),
        Team = AppRole.IdTeam,
        State = TeamTaskState.Open,
        Instructions = Messages.IdTeamInstructions(r, flow.Scope!, flow, settings.TapMaxLifetimeMinutes),
        CreatedAt = clock.UtcNow,
        SinceAt = clock.UtcNow,
    };

    private async Task<Step?> IdTeamAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        var task = flow.IdTeamTask;
        if (task is null || task.State == TeamTaskState.Cancelled)
        {
            var created = NewIdTeamTask(r, flow);
            if (settings.AgentProvisioning)
            {
                // The agent provisions first; the ID team is told what is left (the TAP, or the whole task on a fallback).
                var waiting = (flow with { IdTeamTask = created }).Waiting(WaitingOn.Agent, clock.UtcNow)
                    .Log(clock.UtcNow, Agent, "id-team-task", "ID team task created; the AI agent creates the user, group and membership first.");
                return new Step(r with { Flow = waiting }, []);
            }
            var next = (flow with { IdTeamTask = created }).Waiting(WaitingOn.IdTeam, clock.UtcNow).Log(clock.UtcNow, Agent, "id-team-task", "ID team task created.");
            var project = await ProjectAsync(r, ct);
            var (taskSubject, taskBody) = Messages.TeamTask(r, created, settings);
            return new Step(r with { Flow = next }, [c => notifier.EnqueueAsync(r.ProjectId, r.Id, created.Id, NotificationKind.TeamTask, Recipients.IdTeam(project), taskSubject, taskBody, c)]);
        }
        if (ProvisioningPending(task))
            return writer is null || directory is null ? null : await ProvisionAsync(r, task, ct); // only the worker holds the writer
        if (task.State is TeamTaskState.Open or TeamTaskState.Returned) return await TaskSlaAsync(r, task, ct);
        if (task.State != TeamTaskState.Reported || task.IdReport is null) return null;

        var projectDoc = await ProjectAsync(r, ct);
        var sheet = await sources.ReadMappingSheetAsync(r.ProjectId, ct);
        var pattern = string.IsNullOrWhiteSpace(projectDoc?.GroupNamePattern) ? policy.DefaultGroupNamePattern : projectDoc!.GroupNamePattern!;
        var v = await verifier.IdTeamAsync(r, flow.Scope!, flow, task.IdReport, sheet, pattern, ct);
        if (v.Pending) return null; // the worker (with the Entra reader) finishes it

        if (v.Failed) return Returned(r, task with { Checks = v.Checks }, v, projectDoc,
            v.Has(VerifyCodes.GroupName) ? IssueKind.GroupNameMismatch : IssueKind.VerificationMismatch, "id-team");

        var now = clock.UtcNow;
        if (flow.RoleTeamTask?.State == TeamTaskState.Verified)
        {
            // Back here from final verification: roles were already verified; final verification re-checks them.
            var again = (flow with { IdTeamTask = task with { State = TeamTaskState.Verified, Checks = v.Checks } })
                .Log(now, Agent, "id-team-verified", "AI verified the ID team's correction.")
                .To(RequestStatus.Verifying, WaitingOn.User, now, Agent, "verify-start", "Final verification resumed.");
            return new Step(r with { Flow = again }, [c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.VerificationMismatch], c)]);
        }

        // Passed: the role assignment team gets user, group and role from the response document.
        var record = (await sources.ReadResponseDocumentAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RequestId == r.Id);
        if (record is null) return null;
        var roleTask = new TeamTask
        {
            Id = Ids.Key(r.Id, $"role-team:{flow.Events.Count(e => e.Step == "role-team-task")}"),
            Team = AppRole.RoleTeam,
            State = TeamTaskState.Open,
            Instructions = Messages.RoleTeamInstructions(record),
            CreatedAt = now,
            SinceAt = now,
        };
        var byAgent = settings.AgentRoleAssignment;
        var advanced = (flow with { IdTeamTask = task with { State = TeamTaskState.Verified, Checks = v.Checks }, RoleTeamTask = roleTask })
            .Log(now, Agent, "id-team-verified", "AI verified the ID team's work: user, group, membership, mapping sheet and TAP rules.")
            .To(RequestStatus.WithRoleTeam, byAgent ? WaitingOn.Agent : WaitingOn.RoleTeam, now, Agent, "role-team-task",
                byAgent ? "Role assignment task created from the response document; the AI agent assigns the role if the mapping row allows it."
                        : "Role assignment task created from the response document.");
        var (subject, body) = Messages.TeamTask(r, roleTask, settings);
        var effects = new List<Func<CancellationToken, Task>>
        {
            c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.GroupNameMismatch, IssueKind.VerificationMismatch, IssueKind.TaskSlaBreached], c),
        };
        if (!byAgent)
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, roleTask.Id, NotificationKind.TeamTask, Recipients.RoleTeam(projectDoc), subject, body, c));
        return new Step(r with { Flow = advanced }, effects);
    }

    private Step Returned(AccessRequest r, TeamTask task, Verification v, Project? project, IssueKind kind, string stepName)
    {
        var now = clock.UtcNow;
        var returned = task with
        {
            State = TeamTaskState.Returned,
            ReturnCount = task.ReturnCount + 1,
            LastDifference = v.Differences,
            SinceAt = now,
            PickedUpAt = null,
            PickedUpBy = null,
            RemindedAt = null,
            EscalatedAt = null,
        };
        var waiting = task.Team == AppRole.IdTeam ? WaitingOn.IdTeam : WaitingOn.RoleTeam;
        var flow = r.Flow!;
        var next = (task.Team == AppRole.IdTeam ? flow with { IdTeamTask = returned } : flow with { RoleTeamTask = returned })
            .Waiting(waiting, now)
            .Log(now, Agent, $"{stepName}-returned", $"Returned to the {Messages.Team(task.Team)} with {v.Checks.Count(c => c.Status == CheckStatus.Fail)} difference(s).");
        var (subject, body) = Messages.TaskReturned(r, returned, settings);
        var to = task.Team == AppRole.IdTeam ? Recipients.IdTeam(project) : Recipients.RoleTeam(project);
        return new Step(r with { Flow = next },
        [
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"{task.Id}:returned:{returned.ReturnCount}", NotificationKind.TaskReturned, to, subject, body, c),
            c => issues.RecordAsync(r.ProjectId, r.Id, task.Id, kind, IssueSeverity.Warning,
                kind == IssueKind.GroupNameMismatch ? "Review passed but the ID team created/used a different group name; returned to the ID team."
                : kind == IssueKind.RoleMismatch ? "Role assigned does not match the response document; returned to the role team to correct or revoke."
                : $"Verification found differences; returned to the {Messages.Team(task.Team)}.", v.Differences, c),
        ]);
    }

    private Task<Step?> TaskSlaAsync(AccessRequest r, TeamTask task, CancellationToken ct)
    {
        // "Not picked up past SLA": the clock stops once someone in the team picks it up.
        if (task.PickedUpAt is not null) return Task.FromResult<Step?>(null);
        var waiting = task.Team == AppRole.IdTeam ? WaitingOn.IdTeam : WaitingOn.RoleTeam;
        TeamTask Mark(WorkflowState f) => (task.Team == AppRole.IdTeam ? f.IdTeamTask : f.RoleTeamTask)!;
        WorkflowState Set(WorkflowState f, TeamTask t) => task.Team == AppRole.IdTeam ? f with { IdTeamTask = t } : f with { RoleTeamTask = t };
        return SlaAsync(r, waiting, task.SinceAt, task.RemindedAt, task.EscalatedAt, $"{Messages.Team(task.Team)} task pick-up",
            f => Set(f, Mark(f) with { RemindedAt = clock.UtcNow }), f => Set(f, Mark(f) with { EscalatedAt = clock.UtcNow }),
            IssueKind.TaskSlaBreached, $"{task.Id}:sla:{task.ReturnCount}", ct);
    }

    // ---- Stage 5: role assignment team ---------------------------------------------------------------------------

    private async Task<Step?> RoleTeamAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        var task = flow.RoleTeamTask;
        if (task is null) return null;
        if (RoleAssignmentPending(task))
            return roleWriter is null || directory is null ? null : await AssignRoleAsync(r, task, ct); // only the worker holds the writer
        if (task.State is TeamTaskState.Open or TeamTaskState.Returned) return await TaskSlaAsync(r, task, ct);
        if (task.State != TeamTaskState.Reported || task.RoleReport is null) return null;

        var project = await ProjectAsync(r, ct);
        var record = (await sources.ReadResponseDocumentAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RequestId == r.Id);
        var v = await verifier.RoleTeamAsync(flow.Scope!, record, task.RoleReport, ct);
        if (v.Pending) return null;
        if (v.Failed) return Returned(r, task with { Checks = v.Checks }, v, project, IssueKind.RoleMismatch, "role-team");

        var now = clock.UtcNow;
        var next = (flow with { RoleTeamTask = task with { State = TeamTaskState.Verified, Checks = v.Checks } })
            .Log(now, Agent, "role-team-verified", "AI verified the role assignment matches the approved role exactly.")
            .To(RequestStatus.Verifying, WaitingOn.User, now, Agent, "verify-start", "Final verification: waiting for the user's sign-in, permanent method and TAP removal.");
        var (subject, body) = Messages.AccessReady(r, flow.Scope!);
        return new Step(r with { Flow = next },
        [
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, "access-ready", NotificationKind.AccessReady, Recipients.User(r), subject, body, c),
            c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.RoleMismatch, IssueKind.TaskSlaBreached], c),
        ]);
    }

    // ---- Stage 6: verify, notify, audit --------------------------------------------------------------------------

    private async Task<Step?> FinalAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        var since = flow.IdTeamTask?.IdReport?.Tap?.IssuedAt ?? flow.IdTeamTask?.ReportedAt ?? flow.StageSince;
        var v = await verifier.FinalAsync(r, flow.Scope!, since, ct);
        var now = clock.UtcNow;
        var project = await ProjectAsync(r, ct);
        var effects = new List<Func<CancellationToken, Task>>();
        var changed = !SameChecks(flow.FinalChecks, v.Checks);
        var next = flow with { FinalChecks = v.Checks };

        if (v.Failed)
        {
            // Access is not in place: back to the ID team (membership) — the agent never fixes it itself.
            var task = flow.IdTeamTask!;
            var step = Returned(r with { Flow = next }, task, v, project, IssueKind.VerificationMismatch, "final");
            var back = step.Next.Flow!.To(RequestStatus.WithIdTeam, WaitingOn.IdTeam, now, Agent, "final-failed", "Final verification failed; returned to the ID team.");
            return new Step(step.Next with { Flow = back }, step.Effects);
        }

        if (v.Passed)
        {
            var timings = Reports.TimingText(Reports.Timings(r with { Flow = next }, now));
            var done = next.To(RequestStatus.Completed, WaitingOn.None, now, Agent, "completed", "Verified: sign-in, permanent method, TAP removed. Notifications sent.");
            var scope = flow.Scope!;
            var (s1, b1) = Messages.Completed(r, scope, timings);
            var (s2, b2) = Messages.PmoInformed(r, scope);
            var (s3, b3) = Messages.Closure(r);
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, "completed", NotificationKind.CompletionSummary, Recipients.Owner(r).Concat(Recipients.Security(project)), s1, b1, c));
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, "completed", NotificationKind.PmoInformed, Recipients.Pmo(project), s2, b2, c));
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, "completed", NotificationKind.Closure, Recipients.IdTeam(project).Concat(Recipients.RoleTeam(project)), s3, b3, c));
            effects.Add(c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.TapNotDeleted, IssueKind.VerificationMismatch], c));
            return new Step(r with { Flow = done }, effects);
        }

        // Pending. A TAP left behind after the user signed in and registered a method: the ID team deletes it.
        var tapLeft = v.Checks.Any(c => c.Code == VerifyCodes.FinalTapDeleted && c.Status == CheckStatus.Pending)
                      && v.Checks.Any(c => c.Code == VerifyCodes.FinalPermanentMethod && c.Status == CheckStatus.Pass)
                      && v.Checks.Any(c => c.Code == VerifyCodes.FinalLogin && c.Status == CheckStatus.Pass);
        if (tapLeft && !flow.Events.Any(e => e.Step == "tap-cleanup"))
        {
            var (subject, body) = Messages.TapCleanup(r, settings);
            next = next.Waiting(WaitingOn.IdTeam, now).Log(now, Agent, "tap-cleanup", "TAP still on the account; asked the ID team to delete it.");
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, "tap-cleanup", NotificationKind.TapCleanupNeeded, Recipients.IdTeam(project), subject, body, c));
            effects.Add(c => issues.RecordAsync(r.ProjectId, r.Id, "tap-cleanup", IssueKind.TapNotDeleted, IssueSeverity.Warning, "Temporary Access Pass not deleted after first sign-in.", null, c));
            return new Step(r with { Flow = next }, effects);
        }

        if (now - flow.StageSince > settings.VerifySla && flow.RemindedAt is null)
        {
            var (subject, body) = Messages.Reminder(r, "first sign-in and sign-in method registration", now - flow.StageSince, settings);
            next = (next with { RemindedAt = now }).Log(now, Agent, "reminder", "Final verification pending past SLA; reminded the user and the ID team.");
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, "verify-sla:reminder", NotificationKind.Reminder, Recipients.User(r).Concat(Recipients.IdTeam(project)), subject, body, c));
            return new Step(r with { Flow = next }, effects);
        }
        return changed ? new Step(r with { Flow = next }, effects) : null;
    }

    private static bool SameChecks(IReadOnlyList<CheckResult> a, IReadOnlyList<CheckResult> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Code == p.Second.Code && p.First.Status == p.Second.Status);

    // ---- Auto-fix and failure handling ---------------------------------------------------------------------------

    /// <summary>Safe, idempotent retry (max 3 per request id + step); then Failed-Needs-Dev with a developer report.</summary>
    private async Task<Step?> RetryOrFailAsync(AccessRequest r, string step, string reason, Func<Task<Step?>> retry, CancellationToken ct)
    {
        var key = Ids.Key(r.Id, step);
        var flow = r.Flow!;
        if (flow.AttemptsFor(key) < settings.MaxAttempts)
        {
            var attempt = flow.AttemptsFor(key) + 1;
            var retried = await retry();
            if (retried is null) return null;
            var next = retried.Next.Flow!.WithAttempt(key).Log(clock.UtcNow, Agent, "auto-retry", $"{reason} Auto-retry {attempt} of {settings.MaxAttempts}.");
            retried.Effects.Add(c => issues.RecordAsync(r.ProjectId, r.Id, step, IssueKind.DependencyFailure, IssueSeverity.Warning, reason, $"Auto-retry {attempt} of {settings.MaxAttempts}.", c, IssueStatus.AutoFixed));
            return retried with { Next = retried.Next with { Flow = next }, Stop = true };
        }
        return await FailNeedsDevAsync(r, step, $"{reason} {settings.MaxAttempts} attempts failed.", ct);
    }

    private async Task<Step?> DependencyFailedAsync(AccessRequest r, string category, CancellationToken ct)
    {
        var flow = r.Flow!;
        var step = $"{flow.Status}:{category}";
        var key = Ids.Key(r.Id, step);
        if (flow.AttemptsFor(key) < settings.MaxAttempts)
        {
            var next = flow.WithAttempt(key).Log(clock.UtcNow, Agent, "auto-retry", $"Read failed ({category}); retried on the next agent run.");
            return new Step(r with { Flow = next },
                [c => issues.RecordAsync(r.ProjectId, r.Id, step, IssueKind.DependencyFailure, IssueSeverity.Warning, $"Read failed ({category}); retrying.", null, c, IssueStatus.AutoFixed)],
                Stop: true);
        }
        return await FailNeedsDevAsync(r, step, $"Read failed ({category}) {settings.MaxAttempts} times.", ct);
    }

    private async Task<Step> FailNeedsDevAsync(AccessRequest r, string step, string reason, CancellationToken ct)
    {
        var project = await ProjectAsync(r, ct);
        var flow = (r.Flow! with { LastReason = reason })
            .To(RequestStatus.FailedNeedsDev, WaitingOn.Developer, clock.UtcNow, Agent, "failed-needs-dev", reason);
        var failed = r with { Flow = flow };
        var report = Reports.DeveloperReportFor(failed, clock.UtcNow);
        if (assistant is not null && await assistant.AnalyseAsync("developer-report", report, ct) is { } analysis)
            report += $"\nAI analysis:\n{analysis}";
        return new Step(failed,
        [
            c => issues.RecordAsync(r.ProjectId, r.Id, step, IssueKind.NeedsDeveloper, IssueSeverity.High, reason, report, c),
            c => notifier.EnqueueAsync(r.ProjectId, r.Id, $"failed:{step}", NotificationKind.DeveloperReport, Recipients.Operators(project),
                $"Failed-Needs-Dev: request {Messages.Short(r)}", report, c),
        ]);
    }

    private static TeamTask? Cancel(TeamTask? t) => t is null ? null : t with { State = TeamTaskState.Cancelled };

    private async Task<Project?> ProjectAsync(AccessRequest r, CancellationToken ct) =>
        (await stores.Projects.GetAsync(r.ProjectId, r.ProjectId, ct))?.Value;
}
