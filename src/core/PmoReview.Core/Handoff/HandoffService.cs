using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Handoff;

public sealed record HandoffSettings
{
    /// <summary>Off in the zero-cost pilot; on once the Entra access packages and the sync worker exist.</summary>
    public bool Enabled { get; init; }
    /// <summary>The Reviewer who accepts must not be the person who created or analysed the request.</summary>
    public bool RequireSeparateReviewer { get; init; }
}

/// <summary>
/// Turns an accepted review into an Entra hand-off record: the My Access link for the mapped access package.
/// Nothing here approves or assigns. Entra applies its own approval policy and expiry.
/// </summary>
public sealed class HandoffService(DataStores stores, HandoffSettings settings, IClock clock)
{
    public bool Enabled => settings.Enabled;

    /// <summary>
    /// Called after a Reviewer records a decision. Only "Accepted" on a completed, ready review creates a record;
    /// anything else is ignored. Idempotent per request revision.
    /// </summary>
    public async Task OnDecisionAsync(ReviewRun run, ReviewDecision decision, CancellationToken ct)
    {
        if (!settings.Enabled || decision.Outcome != DecisionOutcome.Accepted) return;
        if (run.ProcessingStatus != ProcessingStatus.Completed || run.Readiness != Readiness.ReadyForHumanReview || run.Result is null) return;

        var candidate = await EvaluateAsync(run, decision, ct);
        var existing = await stores.Handoffs.GetAsync(run.ProjectId, candidate.Id, ct);
        try
        {
            if (existing is null)
                await stores.Handoffs.CreateAsync(candidate, ct);
            else if (existing.Value.Status == HandoffStatus.NotEligible && candidate.Status != HandoffStatus.NotEligible)
                await stores.Handoffs.ReplaceAsync(candidate, existing.ETag, ct); // e.g. a second, separate Reviewer accepted
        }
        catch (Exception e) when (e is DocumentConflictException or ConcurrencyException)
        {
            // Another acceptance won the race; its record stands.
        }
    }

    public async Task<IReadOnlyList<Versioned<EntraHandoff>>> ListForRequestAsync(CallerContext caller, string projectId, string requestId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewRuns);
        // Off = no storage call at all, so the review app works before the 'handoffs' container exists.
        if (!settings.Enabled) return [];
        return (await stores.Handoffs.ListAsync(projectId, ct))
            .Where(h => h.Value.ProjectId == projectId && h.Value.RequestId == requestId)
            .OrderByDescending(h => h.Value.RequestRevision).ToList();
    }

    private async Task<EntraHandoff> EvaluateAsync(ReviewRun run, ReviewDecision decision, CancellationToken ct)
    {
        var s = run.Snapshot;
        var baseRecord = new EntraHandoff
        {
            Id = EntraHandoff.MakeId(run.RequestId, run.RequestRevision),
            ProjectId = run.ProjectId,
            RequestId = run.RequestId,
            RequestRevision = run.RequestRevision,
            ReviewRunId = run.Id,
            AcceptedBy = decision.Reviewer,
            Status = HandoffStatus.AwaitingJoinerRequest,
            JoinerUpn = s.JoinerUpn,
            RequestedStart = s.StartDate,
            RequestedEnd = s.EndDate,
            CreatedAt = clock.UtcNow,
        };
        EntraHandoff NotEligible(string reason) => baseRecord with
        {
            Status = HandoffStatus.NotEligible,
            Reason = reason,
            History = [new HandoffEvent(clock.UtcNow, HandoffStatus.NotEligible, reason)],
        };

        var request = await stores.Requests.GetAsync(run.ProjectId, run.RequestId, ct);
        if (request is null || request.Value.Revision != run.RequestRevision)
            return NotEligible("The request was edited after this review. Analyze the new revision and accept that review.");

        if (settings.RequireSeparateReviewer && (decision.Reviewer == request.Value.CreatedBy || decision.Reviewer == run.InitiatedBy))
            return NotEligible("The accepting Reviewer created or analysed this request. A different Reviewer must accept it.");

        var m = run.Result!.MappingAssessment;
        if (m.Status != MappingStatus.Matched || m.MappingId is null || m.MappingVersion is null)
            return NotEligible("The review did not match exactly one approved role mapping.");

        // The pinned version, not the current one: the hand-off uses exactly what was reviewed.
        var mapping = await stores.Mappings.GetAsync(run.ProjectId, $"{m.MappingId}:v{m.MappingVersion}", ct);
        if (mapping?.Value.AccessPackageId is null || mapping.Value.MyAccessLink is null)
            return NotEligible($"Role mapping {m.MappingId} v{m.MappingVersion} has no Entra access package. A Maintainer must publish a version with the My Access link, then the request is analysed again.");

        if (string.IsNullOrWhiteSpace(s.JoinerUpn))
            return NotEligible("The reviewed revision has no new joiner sign-in name. Add it, Analyze again and accept the new review.");

        return baseRecord with
        {
            MappingId = m.MappingId,
            MappingVersion = m.MappingVersion,
            AccessPackageId = mapping.Value.AccessPackageId,
            MyAccessLink = mapping.Value.MyAccessLink,
            History = [new HandoffEvent(clock.UtcNow, HandoffStatus.AwaitingJoinerRequest, "Review accepted. Send the My Access link to the new joiner.")],
        };
    }
}
