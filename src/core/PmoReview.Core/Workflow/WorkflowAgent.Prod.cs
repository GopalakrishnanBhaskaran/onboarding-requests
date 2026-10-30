using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Resources;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <summary>
/// F3 (v1.1, 30 Oct 2026): Prod read-only by default. A request above Read in Prod waits after the Owner and Security
/// approvals for a ProdApprover's decision on its ProdWrite escalation; the agent never decides it. Before every grant
/// (ID team stage, role team stage) the agent runs <see cref="ProdGuard.MayFulfil"/> again and stops anything that lacks
/// an Approved escalation for this revision and this exact scope.
/// </summary>
public sealed partial class WorkflowAgent
{
    /// <summary>The ProdWrite escalation for the request as it stands (new id when the revision or the scope changed).</summary>
    public Escalation NewProdEscalation(AccessRequest r, ReviewedScope scope, string reason, string? cid) => new()
    {
        Id = ProdGuard.EscalationId(r, scope),
        ProjectId = r.ProjectId,
        Kind = EscalationKind.ProdWrite,
        RequestId = r.Id,
        RequestRevision = r.Revision,
        ScopeFingerprint = ProdGuard.Fingerprint(r, scope),
        User = r.DomainId ?? r.EnterpriseId,
        Group = scope.EntraGroup,
        Level = r.AccessLevel,
        Environment = scope.Environment ?? r.Environment,
        Reason = reason,
        RequestedBy = r.CreatedByName ?? r.CreatedBy,
        CreatedAt = clock.UtcNow,
        CorrelationId = cid,
    };

    /// <summary>Stores the escalation once (a repeat finds it), audits it and asks the ProdApprovers.</summary>
    public async Task RaiseProdEscalationAsync(AccessRequest r, ReviewedScope scope, Escalation e, Project? project, CancellationToken ct)
    {
        try
        {
            await stores.Escalations.CreateAsync(e, ct);
            await stores.AuditEvents.CreateAsync(new AuditEvent
            {
                Id = AuditEvent.NewId(clock.UtcNow), ProjectId = r.ProjectId, At = clock.UtcNow, Actor = Agent, Action = "escalation-raised",
                Target = $"request {r.Id} r{r.Revision}", CorrelationId = e.CorrelationId, Details = $"ProdWrite {e.Id}: {e.Reason}",
            }, ct);
        }
        catch (DocumentConflictException)
        {
            // Raised already (same revision and scope).
        }
        var (subject, body) = Messages.ProdApprovalRequested(r, scope, e, settings);
        await notifier.EnqueueAsync(r.ProjectId, r.Id, $"prod-approval:{e.Id}", NotificationKind.ApprovalRequested, Recipients.ProdApprovers(project), subject, body, ct);
    }

    /// <summary>
    /// After all approvals (Owner, Security and — for Prod above Read — the ProdApprover): the ID team task, and the agent
    /// provisions first when v2.1 is on. The one path into fulfilment, used by the Security and the Prod decisions.
    /// </summary>
    public (WorkflowState Next, List<Func<CancellationToken, Task>> Effects) ToIdTeam(AccessRequest r, WorkflowState next, Project? project, string actor, string step, string note)
    {
        var now = clock.UtcNow;
        var task = NewIdTeamTask(r, next);
        var byAgent = settings.AgentProvisioning;
        next = (next with { IdTeamTask = task })
            .Log(now, Agent, "id-team-task", byAgent ? "ID team task created; the AI agent creates the user, group and membership first." : "ID team task created.")
            .To(RequestStatus.WithIdTeam, byAgent ? WaitingOn.Agent : WaitingOn.IdTeam, now, actor, step,
                byAgent ? $"{note} The AI agent provisions the user, group and membership." : $"{note} Sent to the ID team.");
        var effects = new List<Func<CancellationToken, Task>>();
        if (!byAgent)
        {
            var (subject, body) = Messages.TeamTask(r, task, settings);
            effects.Add(c => notifier.EnqueueAsync(r.ProjectId, r.Id, task.Id, NotificationKind.TeamTask, Recipients.IdTeam(project), subject, body, c));
        }
        return (next, effects);
    }

    private async Task<Escalation?> ProdEscalationAsync(AccessRequest r, ReviewedScope scope, CancellationToken ct) =>
        (await stores.Escalations.GetAsync(r.ProjectId, ProdGuard.EscalationId(r, scope), ct))?.Value;

    /// <summary>
    /// Awaiting Prod approval: the escalation is raised (once), and when a ProdApprover decided it the request moves on
    /// (also when the decision was saved but the request update lost a race). Otherwise SLA reminders; never automatic.
    /// </summary>
    private async Task<Step?> ProdApprovalAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        var scope = flow.Scope!;
        var project = await ProjectAsync(r, ct);
        var row = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var e = await ProdEscalationAsync(r, scope, ct);
        var now = clock.UtcNow;
        if (e is null)
        {
            var reason = ProdGuard.AboveReadInProd(r, scope, ProdGuard.Privileged(scope, row));
            if (reason is null)
            {
                // Not above Read any more (e.g. the review now pins a read-only row): fulfil as any request would.
                var (n, fx) = ToIdTeam(r, flow, project, Agent, "prod-not-needed", "Production access is Read only now: no ProdApprover decision needed.");
                return new Step(r with { Flow = n }, fx);
            }
            var raised = NewProdEscalation(r, scope, reason, null);
            return new Step(r with { Flow = flow.Log(now, Agent, "prod-escalation", $"Production above Read ({reason}): escalation {raised.Id} raised for a ProdApprover.") },
                [c => RaiseProdEscalationAsync(r, scope, raised, project, c)]);
        }
        if (e.Status == EscalationStatus.Open)
            return await SlaAsync(r, WaitingOn.ProdApprover, flow.StageSince, flow.RemindedAt, flow.EscalatedAt, "ProdApprover decision",
                f => f with { RemindedAt = clock.UtcNow }, f => f with { EscalatedAt = clock.UtcNow }, IssueKind.ApprovalSlaBreached, $"prod-sla:{e.Id}", ct);

        var approval = flow.Approvals.LastOrDefault(a => a.Stage == ApprovalStage.Prod && !a.Superseded) is { } given && given.At == e.DecidedAt
            ? null
            : new Approval
            {
                Stage = ApprovalStage.Prod, Decision = e.Status == EscalationStatus.Approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected,
                By = e.DecidedBy ?? "", ByName = e.DecidedByName ?? e.DecidedBy ?? "", At = e.DecidedAt ?? now, Remarks = e.Remarks, ReviewRunId = scope.ReviewRunId,
            };
        var withApproval = approval is null ? flow : flow with { Approvals = [.. flow.Approvals, approval] };
        if (e.Status == EscalationStatus.Rejected)
        {
            var rejected = (withApproval with { LastReason = e.Remarks })
                .To(RequestStatus.Rejected, WaitingOn.None, now, e.DecidedBy ?? Agent, "prod-rejected", $"Rejected by ProdApprover {e.DecidedByName}: {e.Remarks}");
            var (subject, body) = Messages.Rejected(r, withApproval.Approvals.Last(a => a.Stage == ApprovalStage.Prod), settings);
            return new Step(r with { Flow = rejected },
                [c => notifier.EnqueueAsync(r.ProjectId, r.Id, "rejected:Prod", NotificationKind.Rejected, Recipients.Requester(r).Concat(Recipients.Pmo(project)), subject, body, c)]);
        }
        if (!ProdGuard.MayFulfil(r, scope, ProdGuard.Privileged(scope, row), e, out _)) return null; // an Approved decision for another revision never counts
        var (next, effects) = ToIdTeam(r, withApproval, project, e.DecidedBy ?? Agent, "prod-approved",
            $"ProdApprover {e.DecidedByName} approved escalation {e.Id} (production above Read: {e.Reason}).");
        return new Step(r with { Flow = next }, effects);
    }

    /// <summary>
    /// Defence in depth before any grant (ID team and role team stages): a request whose access is above Read in Prod and
    /// has no Approved escalation for this revision and scope goes back to Awaiting Prod approval, with a High issue.
    /// </summary>
    private async Task<Step?> ProdBlockAsync(AccessRequest r, CancellationToken ct)
    {
        var flow = r.Flow!;
        if (flow.Scope is not { } scope) return null;
        var row = (await sources.ReadMappingSheetAsync(r.ProjectId, ct)).FirstOrDefault(x => x.RowId == scope.MappingRowId);
        var privileged = ProdGuard.Privileged(scope, row);
        if (ProdGuard.AboveReadInProd(r, scope, privileged) is not { } reason) return null;
        var e = await ProdEscalationAsync(r, scope, ct);
        if (ProdGuard.MayFulfil(r, scope, privileged, e, out _)) return null;
        var project = await ProjectAsync(r, ct);
        var now = clock.UtcNow;
        var raised = e ?? NewProdEscalation(r, scope, reason, null);
        var next = flow.To(RequestStatus.AwaitingProdApproval, WaitingOn.ProdApprover, now, Agent, "prod-guard",
            $"Stopped before anything was granted at {StatusText.Label(flow.Status)}: production above Read ({reason}) without an approved ProdApprover escalation for revision {r.Revision}. Escalation {raised.Id}.");
        return new Step(r with { Flow = next },
        [
            c => RaiseProdEscalationAsync(r, scope, raised, project, c),
            c => issues.RecordAsync(r.ProjectId, r.Id, $"prod-guard:{raised.Id}", IssueKind.ProdWriteBlocked, IssueSeverity.High,
                $"A production grant above Read reached {StatusText.Label(flow.Status)} without an approved escalation and was stopped; nothing was granted.", null, c),
        ], Stop: true);
    }
}
