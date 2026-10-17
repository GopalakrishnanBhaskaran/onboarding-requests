using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Processing;

public sealed record RetryInput(string? Nonce);

public sealed record RunStarted(string RunId, ProcessingStatus ProcessingStatus, bool Duplicate);

/// <summary>
/// Starting, reading and recovering review runs. Run ids are deterministic per request revision and attempt
/// ("{requestId}.r{rev}.a{n}"), so two concurrent starts of the same attempt collide on id and only one run exists.
/// The workflow agent starts runs (submit, resubmit, Entra cross-check, mapping change, auto-retry); an Operator can
/// retry a failed run.
/// </summary>
public sealed partial class RunService(DataStores stores, IReviewDispatcher dispatcher, ReviewerInfo reviewer, IClock clock, TimeSpan stuckAfter)
{
    /// <summary>Creates the next run for the request's current revision and dispatches it.</summary>
    public async Task<RunStarted> StartAsync(AccessRequest request, string trigger, string initiatedBy, string correlationId, string? retryOf = null)
    {
        var runs = await RunsForRevisionAsync(request.ProjectId, request.Id, request.Revision, CancellationToken.None);
        var latest = runs.LastOrDefault();
        if (latest is not null && latest.ProcessingStatus is ProcessingStatus.Queued or ProcessingStatus.Running)
        {
            if (latest.ProcessingStatus == ProcessingStatus.Queued)
                await dispatcher.DispatchAsync(request.ProjectId, latest.Id, correlationId); // dispatch may have been lost; claim is ETag-guarded
            return await ResponseAsync(request.ProjectId, latest.Id, duplicate: true);
        }

        var attempt = runs.Count;
        var snapshot = SnapshotBuilder.Build(request, clock.UtcNow);
        var run = new ReviewRun
        {
            Id = $"{request.Id}.r{request.Revision}.a{attempt}",
            ProjectId = request.ProjectId,
            RequestId = request.Id,
            Snapshot = snapshot,
            SnapshotHash = SnapshotBuilder.Hash(snapshot),
            RequestRevision = request.Revision,
            InitiatedBy = initiatedBy,
            ProcessingStatus = ProcessingStatus.Queued,
            ReviewerProvider = reviewer.Provider,
            ReviewerVersion = reviewer.Version,
            PromptVersion = reviewer.PromptVersion,
            CorrelationId = correlationId,
            QueuedAt = clock.UtcNow,
            RetryOfRunId = retryOf,
            IdempotencyKey = $"{request.Id}:{request.Revision}:{attempt}",
            Trigger = trigger,
        };

        try
        {
            await stores.Runs.CreateAsync(run, CancellationToken.None);
        }
        catch (DocumentConflictException)
        {
            var again = await RunsForRevisionAsync(request.ProjectId, request.Id, request.Revision, CancellationToken.None);
            return await ResponseAsync(request.ProjectId, again[^1].Id, duplicate: true);
        }

        // Accepted. Processing must not depend on the caller staying connected.
        await dispatcher.DispatchAsync(request.ProjectId, run.Id, correlationId);
        return await ResponseAsync(request.ProjectId, run.Id, duplicate: false);
    }

    /// <summary>Operator: retry a failed run of the request's current revision.</summary>
    public async Task<RunStarted> RetryAsync(CallerContext caller, string projectId, string runId, RetryInput input, string correlationId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.RecoverRuns);
        ValidNonce(input.Nonce);
        var failed = (await LoadRunAsync(projectId, runId, ct)).Value;
        if (failed.ProcessingStatus != ProcessingStatus.Failed)
            throw AppException.Conflict("not_failed", "Only a failed run can be retried.");
        var request = await stores.Requests.GetAsync(projectId, failed.RequestId, ct);
        if (request is null || request.Value.ProjectId != projectId) throw AppException.NotFound("Request");
        if (request.Value.Revision != failed.RequestRevision)
            throw AppException.Conflict("request_changed", "The request has changed since this run.");
        return await StartAsync(request.Value, "operator-retry", caller.UserId, correlationId, retryOf: failed.Id);
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

    public async Task<ReviewRun?> LatestAsync(string projectId, string requestId, CancellationToken ct) =>
        (await stores.Runs.ListAsync(projectId, ct))
            .Select(r => r.Value)
            .Where(r => r.ProjectId == projectId && r.RequestId == requestId)
            .OrderByDescending(r => r.QueuedAt).ThenByDescending(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Operator: runs stuck in Queued/Running longer than the threshold become Failed(Timeout).</summary>
    public async Task<IReadOnlyList<string>> RecoverStuckAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.RecoverRuns);
        return await RecoverStuckAsync(projectId, ct);
    }

    public async Task<IReadOnlyList<string>> RecoverStuckAsync(string projectId, CancellationToken ct)
    {
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

    private async Task<RunStarted> ResponseAsync(string projectId, string runId, bool duplicate)
    {
        var run = await stores.Runs.GetAsync(projectId, runId, CancellationToken.None);
        return new RunStarted(runId, run?.Value.ProcessingStatus ?? ProcessingStatus.Queued, duplicate);
    }

    private async Task<List<ReviewRun>> RunsForRevisionAsync(string projectId, string requestId, int revision, CancellationToken ct) =>
        (await stores.Runs.ListAsync(projectId, ct))
            .Select(r => r.Value)
            .Where(r => r.RequestId == requestId && r.RequestRevision == revision)
            .OrderBy(r => r.QueuedAt).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

    private async Task<Versioned<ReviewRun>> LoadRunAsync(string projectId, string runId, CancellationToken ct)
    {
        var r = await stores.Runs.GetAsync(projectId, runId, ct);
        if (r is null || r.Value.ProjectId != projectId) throw AppException.NotFound("Review run");
        return r;
    }

    private static void ValidNonce(string? nonce)
    {
        if (nonce is null || !Nonce().IsMatch(nonce)) throw AppException.Invalid("nonce is required (8-64 letters, digits or dashes).");
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex Nonce();
}
