using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Processing;

public sealed record AnalyzeInput(string? Nonce);

public sealed record DecisionInput(DecisionOutcome? Outcome, string? Comment);

public sealed record AnalyzeResponse(string RunId, ProcessingStatus ProcessingStatus, bool Duplicate);

public sealed record AttentionItem(string ProjectId, string RunId, string RequestId, int RequestRevision, ProcessingStatus ProcessingStatus, Readiness? Readiness, ErrorCategory? ErrorCategory, DateTimeOffset QueuedAt);

/// <summary>
/// Starting, retrying, reading and recovering review runs; recording reviewer feedback.
/// Run ids are deterministic per request revision and attempt ("{requestId}.r{rev}.a{n}"), so two
/// concurrent Analyze calls for the same revision collide on id and only one run is ever created.
/// </summary>
public sealed partial class RunService(DataStores stores, IReviewDispatcher dispatcher, ReviewerInfo reviewer, IClock clock, TimeSpan stuckAfter)
{
    public async Task<AnalyzeResponse> AnalyzeAsync(CallerContext caller, string projectId, string requestId, AnalyzeInput input, string correlationId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.AnalyzeRequests);
        var nonce = ValidNonce(input.Nonce);
        var request = await LoadRequestAsync(projectId, requestId, ct);
        var revision = request.Value.Revision;

        // One run per revision. Re-analysing an unchanged revision returns the existing run (use Retry after a failure).
        var existing = await RunsForRevisionAsync(projectId, requestId, revision, ct);
        if (existing.Count > 0)
        {
            var latest = existing[^1];
            if (latest.ProcessingStatus == ProcessingStatus.Queued)
                await dispatcher.DispatchAsync(projectId, latest.Id, correlationId); // dispatch may have been lost; claim is ETag-guarded
            return await ResponseAsync(projectId, latest.Id, duplicate: true);
        }

        return await CreateAndDispatchAsync(caller, request.Value, attempt: 0, nonce, retryOf: null, correlationId, ct);
    }

    public async Task<AnalyzeResponse> RetryAsync(CallerContext caller, string projectId, string runId, AnalyzeInput input, string correlationId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.AnalyzeRequests);
        var nonce = ValidNonce(input.Nonce);
        var failed = (await LoadRunAsync(projectId, runId, ct)).Value;
        if (failed.ProcessingStatus != ProcessingStatus.Failed)
            throw new AppException(ErrorKind.Conflict, "not_failed", "Only a failed run can be retried.");

        var request = await LoadRequestAsync(projectId, failed.RequestId, ct);
        if (request.Value.Revision != failed.RequestRevision)
            throw new AppException(ErrorKind.Conflict, "request_changed", "The request has changed since this run. Analyze the request again instead.");

        var runs = await RunsForRevisionAsync(projectId, failed.RequestId, failed.RequestRevision, ct);
        if (runs[^1].Id != failed.Id)
            return await ResponseAsync(projectId, runs[^1].Id, duplicate: true); // someone already retried

        return await CreateAndDispatchAsync(caller, request.Value, attempt: runs.Count, nonce, retryOf: failed.Id, correlationId, ct);
    }

    public async Task<Versioned<ReviewRun>> GetAsync(CallerContext caller, string projectId, string runId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewRuns);
        return await LoadRunAsync(projectId, runId, ct);
    }

    public async Task<IReadOnlyList<Versioned<ReviewRun>>> ListForRequestAsync(CallerContext caller, string projectId, string requestId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewRuns);
        return (await stores.Runs.ListAsync(projectId, ct))
            .Where(r => r.Value.ProjectId == projectId && r.Value.RequestId == requestId)
            .OrderByDescending(r => r.Value.QueuedAt).ThenByDescending(r => r.Value.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Failed runs and runs needing manual review, latest run per request, across the caller's Reviewer projects.</summary>
    public async Task<IReadOnlyList<AttentionItem>> AttentionAsync(CallerContext caller, CancellationToken ct)
    {
        var items = new List<AttentionItem>();
        foreach (var projectId in caller.ProjectsWith(Permission.RecordDecision))
        {
            var latest = (await stores.Runs.ListAsync(projectId, ct))
                .Select(r => r.Value)
                .Where(r => r.ProjectId == projectId)
                .GroupBy(r => r.RequestId)
                .Select(g => g.OrderByDescending(r => r.QueuedAt).ThenByDescending(r => r.Id, StringComparer.Ordinal).First());
            items.AddRange(latest
                .Where(r => r.ProcessingStatus == ProcessingStatus.Failed || r.Readiness == Readiness.ManualReviewRequired)
                .Select(r => new AttentionItem(projectId, r.Id, r.RequestId, r.RequestRevision, r.ProcessingStatus, r.Readiness, r.ErrorCategory, r.QueuedAt)));
        }
        return items.OrderByDescending(i => i.QueuedAt).ToList();
    }

    public async Task<Versioned<ReviewDecision>> RecordDecisionAsync(CallerContext caller, string projectId, string runId, DecisionInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.RecordDecision);
        var outcome = input.Outcome ?? throw AppException.Invalid("outcome is required (Accepted, Corrected or Escalated).");
        var comment = Services.RequestService.Text(input.Comment, "comment", 1000);
        if (outcome != DecisionOutcome.Accepted && comment is null)
            throw AppException.Invalid("A comment is required when the outcome is Corrected or Escalated.");

        var run = (await LoadRunAsync(projectId, runId, ct)).Value;
        if (run.ProcessingStatus is not (ProcessingStatus.Completed or ProcessingStatus.Superseded))
            throw new AppException(ErrorKind.Conflict, "run_not_complete", "Feedback can only be recorded on a completed review.");

        try
        {
            return await stores.Decisions.CreateAsync(new ReviewDecision
            {
                Id = Guid.NewGuid().ToString("n"),
                ProjectId = projectId,
                ReviewRunId = runId,
                Reviewer = caller.UserId,
                Outcome = outcome,
                Comment = comment,
                Timestamp = clock.UtcNow,
            }, ct);
        }
        catch (DocumentConflictException)
        {
            throw new AppException(ErrorKind.Conflict, "decision_exists", "You have already recorded feedback for this review.");
        }
    }

    public async Task<IReadOnlyList<Versioned<ReviewDecision>>> ListDecisionsAsync(CallerContext caller, string projectId, string runId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewRuns);
        return (await stores.Decisions.ListAsync(projectId, ct))
            .Where(d => d.Value.ProjectId == projectId && d.Value.ReviewRunId == runId)
            .OrderBy(d => d.Value.Timestamp).ToList();
    }

    /// <summary>Operator: runs stuck in Queued/Running longer than the threshold become Failed(Timeout).</summary>
    public async Task<IReadOnlyList<string>> RecoverStuckAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.RecoverRuns);
        var cutoff = clock.UtcNow - stuckAfter;
        var recovered = new List<string>();
        foreach (var v in await stores.Runs.ListAsync(projectId, ct))
        {
            var r = v.Value;
            var since = r.StartedAt ?? r.QueuedAt;
            if (r.ProjectId != projectId || r.ProcessingStatus is not (ProcessingStatus.Queued or ProcessingStatus.Running) || since > cutoff) continue;
            try
            {
                await stores.Runs.ReplaceAsync(r with { ProcessingStatus = ProcessingStatus.Failed, ErrorCategory = ErrorCategory.Timeout, CompletedAt = clock.UtcNow }, v.ETag, ct);
                recovered.Add(r.Id);
            }
            catch (ConcurrencyException)
            {
                // Finished while we looked; leave its truthful status.
            }
        }
        return recovered;
    }

    private async Task<AnalyzeResponse> CreateAndDispatchAsync(
        CallerContext caller, AccessRequest request, int attempt, string nonce, string? retryOf, string correlationId, CancellationToken ct)
    {
        var snapshot = SnapshotBuilder.Build(request, clock.UtcNow);
        var run = new ReviewRun
        {
            Id = $"{request.Id}.r{request.Revision}.a{attempt}",
            ProjectId = request.ProjectId,
            RequestId = request.Id,
            Snapshot = snapshot,
            SnapshotHash = SnapshotBuilder.Hash(snapshot),
            RequestRevision = request.Revision,
            InitiatedBy = caller.UserId,
            ProcessingStatus = ProcessingStatus.Queued,
            ReviewerProvider = reviewer.Provider,
            ReviewerVersion = reviewer.Version,
            PromptVersion = reviewer.PromptVersion,
            CorrelationId = correlationId,
            QueuedAt = clock.UtcNow,
            RetryOfRunId = retryOf,
            IdempotencyKey = $"{request.Id}:{request.Revision}:{nonce}",
        };

        try
        {
            await stores.Runs.CreateAsync(run, ct);
        }
        catch (DocumentConflictException)
        {
            // Concurrent Analyze/Retry for the same revision won the race (same id) or same nonce reused.
            var runs = await RunsForRevisionAsync(request.ProjectId, request.Id, request.Revision, CancellationToken.None);
            return await ResponseAsync(request.ProjectId, runs[^1].Id, duplicate: true);
        }

        // Accepted. Processing must not depend on the caller staying connected.
        await dispatcher.DispatchAsync(request.ProjectId, run.Id, correlationId);
        return await ResponseAsync(request.ProjectId, run.Id, duplicate: false);
    }

    private async Task<AnalyzeResponse> ResponseAsync(string projectId, string runId, bool duplicate)
    {
        var run = await stores.Runs.GetAsync(projectId, runId, CancellationToken.None);
        return new AnalyzeResponse(runId, run?.Value.ProcessingStatus ?? ProcessingStatus.Queued, duplicate);
    }

    private async Task<List<ReviewRun>> RunsForRevisionAsync(string projectId, string requestId, int revision, CancellationToken ct) =>
        (await stores.Runs.ListAsync(projectId, ct))
            .Select(r => r.Value)
            .Where(r => r.RequestId == requestId && r.RequestRevision == revision)
            .OrderBy(r => r.QueuedAt).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

    private async Task<Versioned<AccessRequest>> LoadRequestAsync(string projectId, string requestId, CancellationToken ct)
    {
        var r = await stores.Requests.GetAsync(projectId, requestId, ct);
        if (r is null || r.Value.ProjectId != projectId) throw AppException.NotFound("Request");
        return r;
    }

    private async Task<Versioned<ReviewRun>> LoadRunAsync(string projectId, string runId, CancellationToken ct)
    {
        var r = await stores.Runs.GetAsync(projectId, runId, ct);
        if (r is null || r.Value.ProjectId != projectId) throw AppException.NotFound("Review run");
        return r;
    }

    private static string ValidNonce(string? nonce) =>
        nonce is not null && Nonce().IsMatch(nonce) ? nonce : throw AppException.Invalid("nonce is required (8-64 letters, digits or dashes).");

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex Nonce();
}
