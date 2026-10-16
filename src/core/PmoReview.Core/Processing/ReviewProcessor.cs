using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Processing;

/// <summary>
/// How a queued run gets processed. The pilot dispatches inline; the paid environment uses a queue.
/// Takes projectId as well as the run id because runs are partitioned by project: a single-partition
/// read is cheap, a cross-partition lookup by id is not (documented deviation from the prompt's signature).
/// </summary>
public interface IReviewDispatcher
{
    string Name { get; }

    Task DispatchAsync(string projectId, string reviewRunId, string correlationId);
}

/// <summary>
/// The only code path allowed to write snapshot results, versions or status.
/// Queued → (claim, ETag) Running → Completed | Failed | Superseded.
/// </summary>
public sealed class ReviewProcessor(DataStores stores, ReviewEngine engine, IClock clock, TimeSpan budget, ILogger<ReviewProcessor> logger)
{
    public async Task ProcessAsync(string projectId, string runId, string correlationId)
    {
        var queued = await stores.Runs.GetAsync(projectId, runId);
        if (queued is null || queued.Value.ProcessingStatus != ProcessingStatus.Queued) return; // already handled: idempotent

        Versioned<ReviewRun> running;
        try
        {
            running = await stores.Runs.ReplaceAsync(queued.Value with { ProcessingStatus = ProcessingStatus.Running, StartedAt = clock.UtcNow }, queued.ETag);
        }
        catch (ConcurrencyException)
        {
            logger.LogInformation("run {RunId} already claimed cid={CorrelationId}", runId, correlationId);
            return;
        }

        var run = running.Value;
        // Own token: the caller closing the browser must not cancel accepted work. Only the budget does.
        using var cts = new CancellationTokenSource(budget);
        try
        {
            if (!SnapshotBuilder.Verify(run.Snapshot, run.SnapshotHash))
                throw new ReviewFailedException(ErrorCategory.Internal, "Snapshot integrity check failed.");

            var outcome = await engine.RunAsync(run.Snapshot, run.Id, cts.Token);

            var request = await stores.Requests.GetAsync(projectId, run.RequestId, cts.Token);
            var currentRevision = request?.Value.Revision;
            var superseded = currentRevision != run.RequestRevision;

            var done = run with
            {
                ProcessingStatus = superseded ? ProcessingStatus.Superseded : ProcessingStatus.Completed,
                SupersededByRevision = superseded ? currentRevision : null,
                Readiness = outcome.Result.Readiness,
                Result = outcome.Result,
                RuleVersions = outcome.RuleVersions,
                MappingVersions = outcome.MappingVersions,
                ReviewerProvider = outcome.Reviewer.Provider,
                ReviewerVersion = outcome.Reviewer.Version,
                PromptVersion = outcome.Reviewer.PromptVersion,
                CompletedAt = clock.UtcNow,
            };
            await CommitAsync(running, done, correlationId);
        }
        catch (Exception e)
        {
            var category = e switch
            {
                ReviewFailedException rf => rf.Category,
                OperationCanceledException => ErrorCategory.Timeout,
                _ => ErrorCategory.Internal,
            };
            logger.LogWarning("run {RunId} failed {Category} {ExceptionType} cid={CorrelationId}", runId, category, e.GetType().Name, correlationId);
            await CommitAsync(running, run with { ProcessingStatus = ProcessingStatus.Failed, ErrorCategory = category, CompletedAt = clock.UtcNow }, correlationId);
        }
    }

    private async Task CommitAsync(Versioned<ReviewRun> claimed, ReviewRun final, string correlationId)
    {
        try
        {
            await stores.Runs.ReplaceAsync(final, claimed.ETag, CancellationToken.None);
            logger.LogInformation("run {RunId} -> {Status} {Readiness} cid={CorrelationId}", final.Id, final.ProcessingStatus, final.Readiness, correlationId);
        }
        catch (ConcurrencyException)
        {
            // An Operator marked it Failed(Timeout) meanwhile. Their status stands; do not overwrite.
            logger.LogWarning("run {RunId} changed during processing; result discarded cid={CorrelationId}", final.Id, correlationId);
        }
    }
}

/// <summary>ACTIVE in the pilot: processes inside the Analyze HTTP call, within the processing budget.</summary>
public sealed class InlineDispatcher(ReviewProcessor processor) : IReviewDispatcher
{
    public string Name => "Inline";

    public Task DispatchAsync(string projectId, string reviewRunId, string correlationId) =>
        processor.ProcessAsync(projectId, reviewRunId, correlationId);
}

public sealed class DispatchMessage
{
    public required string ProjectId { get; init; }
    public required string ReviewRunId { get; init; }
    public required string CorrelationId { get; init; }

    public static DispatchMessage? Parse(string json)
    {
        try
        {
            return AppJson.Deserialize<DispatchMessage>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
