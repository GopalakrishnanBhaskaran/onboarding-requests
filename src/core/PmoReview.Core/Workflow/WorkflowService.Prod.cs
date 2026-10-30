using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Http;
using PmoReview.Core.Resources;
using PmoReview.Core.Security;
using PmoReview.Core.Services;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <param name="Decision">Approved or Rejected.</param>
/// <param name="RequestRevision">The request revision the ProdApprover read; a decision on another revision is refused.</param>
public sealed record EscalationDecisionInput(EscalationStatus? Decision, string? Remarks, int? RequestRevision);

public sealed record EscalationDecisionResult(Item<Escalation> Escalation, RequestView? Request);

/// <summary>F3: the ProdApprover's decision, the escalation list and the guard on team reports.</summary>
public sealed partial class WorkflowService
{
    public async Task<IReadOnlyList<Item<Escalation>>> EscalationsAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewEscalations);
        return (await stores.Escalations.ListAsync(projectId, ct)).Where(e => e.Value.ProjectId == projectId)
            .OrderBy(e => e.Value.Status).ThenByDescending(e => e.Value.CreatedAt).Select(Endpoints.ToItem).ToList();
    }

    /// <summary>
    /// A ProdApprover approves or rejects a ProdWrite escalation. Never the requester, the Owner approver or the Security
    /// approver of the request; only for the request revision and scope the escalation was raised for (a changed request
    /// gets a new escalation). Approved: the request goes to fulfilment through the normal audited path (ID team task, the
    /// agent's checks). Rejected: the request is closed.
    /// </summary>
    public async Task<EscalationDecisionResult> DecideProdEscalationAsync(CallerContext caller, string projectId, string escalationId, EscalationDecisionInput input,
        string cid, CancellationToken ct)
    {
        caller.Require(projectId, Permission.DecideProdEscalation);
        var stored = await stores.Escalations.GetAsync(projectId, escalationId, ct) ?? throw AppException.NotFound("Escalation");
        var e = stored.Value;
        if (e.Kind != EscalationKind.ProdWrite || e.RequestId is null)
            throw AppException.Conflict("not_prod_escalation", "Only production (ProdWrite) escalations are decided here.");
        if (e.Status != EscalationStatus.Open)
            throw AppException.Conflict("already_decided", $"This escalation was already {e.Status.ToString().ToLowerInvariant()} by {e.DecidedByName ?? e.DecidedBy}.");
        if (input.Decision is not (EscalationStatus.Approved or EscalationStatus.Rejected))
            throw AppException.Invalid("decision is required (Approved or Rejected).");
        var revision = input.RequestRevision ?? throw AppException.Invalid("requestRevision is required: the request revision you read.");
        var remarks = SecretMasker.Apply(RequestService.Text(input.Remarks, "remarks", 1000));
        if (input.Decision == EscalationStatus.Rejected && remarks is null) throw AppException.Invalid("Remarks are required when rejecting.");

        var current = await requests.LoadAsync(projectId, e.RequestId, ct);
        var r = current.Value;
        var flow = r.Flow ?? throw AppException.Conflict("legacy_request", "This is a v1 request.");
        if (flow.Status != RequestStatus.AwaitingProdApproval || flow.Scope is not { } scope)
            throw AppException.Conflict("not_awaiting_prod", $"The request is {StatusText.Label(flow.Status)}; no Prod decision is pending.");
        // Stale: the request (revision) or what it grants (scope) changed since the escalation was raised or read.
        if (e.RequestRevision != r.Revision || revision != r.Revision || e.ScopeFingerprint != ProdGuard.Fingerprint(r, scope) || e.Id != ProdGuard.EscalationId(r, scope))
            throw AppException.Conflict("stale_escalation",
                $"The request changed after this escalation was raised (now revision {r.Revision}). Nothing was approved; read the current request and its escalation.");
        // Four eyes: whoever submitted or resubmitted any revision, the person who gets the access, and the people who
        // approved the earlier stages never decide the Prod stage.
        if (SubmittedBy(r, caller))
            throw new AppException(ErrorKind.Forbidden, "requester_cannot_approve", "You submitted or changed this request; another ProdApprover must decide it.");
        if (caller.Is(r.EnterpriseId) || caller.Is(r.DomainId))
            throw new AppException(ErrorKind.Forbidden, "beneficiary_cannot_approve", "This request gives you access; another ProdApprover must decide it.");
        if (flow.CurrentApproval(ApprovalStage.Owner)?.By == caller.UserId || flow.CurrentApproval(ApprovalStage.Security)?.By == caller.UserId)
            throw AppException.Conflict("same_approver", "You approved an earlier stage of this request; another ProdApprover must decide the Prod stage.");
        var sheet = await sources.ReadMappingSheetAsync(projectId, ct);
        if (!MappingSheet.AsReviewed(sheet, r.Resource, scope.SheetFingerprint, scope.MappingRowId, clock.UtcNow))
        {
            await agent.AdvanceAsync(projectId, r.Id, cid, ct);
            throw AppException.Conflict("mapping_changed", "The mapping sheet changed after the AI review. The review is being re-run; your decision was not recorded.");
        }

        var now = clock.UtcNow;
        Versioned<Escalation> decided;
        try
        {
            // The decision first: if the request update below loses a race, the agent applies the recorded decision.
            decided = await stores.Escalations.ReplaceAsync(e with
            {
                Status = input.Decision.Value, DecidedBy = caller.UserId, DecidedByName = caller.DisplayName, DecidedAt = now, Remarks = remarks, CorrelationId = cid,
            }, stored.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Stale();
        }
        await stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(now), ProjectId = projectId, At = now, Actor = caller.DisplayName, Action = $"prod-escalation-{input.Decision.Value.ToString().ToLowerInvariant()}",
            Target = $"request {r.Id} r{r.Revision}", CorrelationId = cid, Details = $"{e.Id}: {e.Reason}{(remarks is null ? "" : $" — {remarks}")}",
        }, ct);

        var approval = new Approval
        {
            Stage = ApprovalStage.Prod, Decision = input.Decision == EscalationStatus.Approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected,
            By = caller.UserId, ByName = caller.DisplayName, At = now, Remarks = remarks, ReviewRunId = scope.ReviewRunId,
        };
        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value;
        var next = flow with { Approvals = [.. flow.Approvals, approval] };
        var effects = new List<Func<CancellationToken, Task>>();
        Func<ResponseRecord, ResponseRecord> record;
        if (input.Decision == EscalationStatus.Approved)
        {
            var (fulfil, fx) = agent.ToIdTeam(r, next, project, caller.UserId, "prod-approved",
                $"ProdApprover {caller.DisplayName} approved escalation {e.Id} (production above Read: {e.Reason}).");
            next = fulfil;
            effects.AddRange(fx);
            record = x => x with { State = ResponseState.Approved };
        }
        else
        {
            next = (next with { LastReason = remarks }).To(RequestStatus.Rejected, WaitingOn.None, now, caller.UserId, "prod-rejected", $"Rejected by ProdApprover: {remarks}");
            var (subject, body) = Messages.Rejected(r, approval, settings);
            effects.Add(c => notifier.EnqueueAsync(projectId, r.Id, "rejected:Prod", NotificationKind.Rejected, Recipients.Requester(r).Concat(Recipients.Pmo(project)), subject, body, c));
            record = x => x with { State = ResponseState.Rejected };
        }
        try
        {
            await stores.Requests.ReplaceAsync(r with { Flow = next, UpdatedAt = now }, current.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            // Someone acted meanwhile: the recorded decision is applied by the agent below (or on its next run).
            effects.Clear();
        }
        await UpdateResponseRecordAsync(projectId, r.Id, caller.UserId, record, ct);
        foreach (var effect in effects) await effect(ct);
        await agent.AdvanceAsync(projectId, r.Id, cid, ct);
        var view = caller.Has(projectId, Permission.ViewRequests) ? await ViewAsync(caller, projectId, r.Id, ct) : null;
        return new EscalationDecisionResult(Endpoints.ToItem(decided), view);
    }

    /// <summary>The caller created the request, submitted or resubmitted one of its revisions, or last changed it.</summary>
    private static bool SubmittedBy(AccessRequest r, CallerContext caller) =>
        caller.UserId == r.CreatedBy || caller.Is(r.CreatedByName) || caller.UserId == r.UpdatedBy
        || (r.Flow?.Events ?? []).Any(e => e.Step is "submit" or "resubmit" && e.Actor == caller.UserId);

    /// <summary>A team report for Prod above Read is refused until the ProdWrite escalation of this revision and scope is Approved.</summary>
    private async Task RequireProdApprovedAsync(AccessRequest r, CancellationToken ct)
    {
        var scope = r.Flow?.Scope;
        if (scope is null) return;
        var row = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var e = (await stores.Escalations.GetAsync(r.ProjectId, ProdGuard.EscalationId(r, scope), ct))?.Value;
        if (!ProdGuard.MayFulfil(r, scope, row, e, out var reason))
            throw AppException.Conflict("prod_write_not_approved",
                $"Production above Read ({reason}) needs an approved ProdApprover escalation for this request revision before anything is granted or recorded.");
    }
}
