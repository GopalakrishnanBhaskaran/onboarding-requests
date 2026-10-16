using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Dispatch;
using PmoReview.Adapters.Review;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Phase 3 exit gate: duplicate, concurrent, edit-during-processing, timeout and retry leave truthful statuses.</summary>
public class ProcessingTests
{
    private static async Task<IReadOnlyList<ReviewRun>> Runs(TestApp app, string requestId) =>
        (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Select(r => r.Value).Where(r => r.RequestId == requestId).ToList();

    [Fact]
    public async Task Analyze_completes_inline_with_snapshot_and_pinned_versions()
    {
        var app = TestApp.With();
        var id = await app.CreateRequest();
        var a = await app.Analyze(id);

        Assert.Equal(202, a.Status);
        Assert.Equal("Completed", a.Json.GetProperty("processingStatus").GetString());
        Assert.False(a.Json.GetProperty("duplicate").GetBoolean());

        var run = (await app.Run(a.Json.GetProperty("runId").GetString()!)).Value;
        Assert.Equal("ReadyForHumanReview", run.GetProperty("readiness").GetString());
        Assert.Matches("^[0-9a-f]{64}$", run.GetProperty("snapshotHash").GetString());
        Assert.Equal(1, run.GetProperty("requestRevision").GetInt32());
        Assert.Equal("RuleOnly", run.GetProperty("reviewerProvider").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, run.GetProperty("promptVersion").ValueKind);
        Assert.True(run.GetProperty("ruleVersions").GetArrayLength() > 0);
        Assert.Equal(TestApp.Alice, run.GetProperty("initiatedBy").GetString());
    }

    [Fact]
    public async Task Duplicate_submissions_do_not_create_duplicate_results()
    {
        var app = TestApp.With();
        var id = await app.CreateRequest();
        var first = await app.Analyze(id, "nonce-aaaa1");
        var sameNonce = await app.Analyze(id, "nonce-aaaa1");
        var otherNonce = await app.Analyze(id, "nonce-bbbb2");

        var runId = first.Json.GetProperty("runId").GetString();
        Assert.Equal(runId, sameNonce.Json.GetProperty("runId").GetString());
        Assert.Equal(runId, otherNonce.Json.GetProperty("runId").GetString());
        Assert.True(otherNonce.Json.GetProperty("duplicate").GetBoolean());
        Assert.Single(await Runs(app, id));
    }

    [Fact]
    public async Task Concurrent_analyze_creates_one_run()
    {
        var model = new FakeModel(async (ctx, ct) =>
        {
            await Task.Delay(150, ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        var app = TestApp.With(model);
        var id = await app.CreateRequest();

        var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => app.Analyze(id, $"nonce-par-{i:D4}"))));

        Assert.All(calls, c => Assert.Equal(202, c.Status));
        Assert.Single(calls.Select(c => c.Json.GetProperty("runId").GetString()).Distinct());
        var runs = await Runs(app, id);
        Assert.Single(runs);
        Assert.Equal(ProcessingStatus.Completed, runs[0].ProcessingStatus);
    }

    [Fact]
    public async Task Edit_during_processing_marks_run_superseded_and_keeps_history()
    {
        TestApp? app = null;
        string? requestId = null;
        var edited = false;
        var model = new FakeModel(async (ctx, ct) =>
        {
            if (!edited)
            {
                edited = true;
                var req = (await app!.Stores.Requests.GetAsync(TestApp.Alpha, requestId!, ct))!;
                await app.Stores.Requests.ReplaceAsync(req.Value with { Manager = "SAMPLE Manager Two", Revision = req.Value.Revision + 1 }, req.ETag, ct);
            }
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        app = TestApp.With(model);
        requestId = await app.CreateRequest();

        var first = await app.Analyze(requestId, "nonce-edit-1");
        Assert.Equal("Superseded", first.Json.GetProperty("processingStatus").GetString());
        var r1 = (await app.Run(first.Json.GetProperty("runId").GetString()!)).Value;
        Assert.Equal(2, r1.GetProperty("supersededByRevision").GetInt32());
        Assert.Equal(1, r1.GetProperty("requestRevision").GetInt32());

        var second = await app.Analyze(requestId, "nonce-edit-2");
        Assert.Equal("Completed", second.Json.GetProperty("processingStatus").GetString());

        var history = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests/{requestId}/runs");
        var statuses = history.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetProperty("processingStatus").GetString()).ToList();
        Assert.Equal(["Completed", "Superseded"], statuses);
    }

    [Fact]
    public async Task Timeout_fails_run_and_retry_links_new_run()
    {
        var calls = 0;
        var model = new FakeModel(async (ctx, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        var app = TestApp.With(model, new PilotSettings { ProcessingBudgetSeconds = 1 });
        var id = await app.CreateRequest();

        var a = await app.Analyze(id, "nonce-time-1");
        var failedId = a.Json.GetProperty("runId").GetString()!;
        var failed = (await app.Run(failedId)).Value;
        Assert.Equal("Failed", failed.GetProperty("processingStatus").GetString());
        Assert.Equal("Timeout", failed.GetProperty("errorCategory").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, failed.GetProperty("result").ValueKind);

        // Analyze again on the same revision returns the failed run; Retry is the explicit path.
        Assert.Equal(failedId, (await app.Analyze(id, "nonce-time-2")).Json.GetProperty("runId").GetString());

        var retry = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/runs/{failedId}/retry", new { nonce = "nonce-retry-1" });
        Assert.Equal(202, retry.Status);
        var retryRun = (await app.Run(retry.Json.GetProperty("runId").GetString()!)).Value;
        Assert.Equal("Completed", retryRun.GetProperty("processingStatus").GetString());
        Assert.Equal(failedId, retryRun.GetProperty("retryOfRunId").GetString());

        var again = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/runs/{retryRun.GetProperty("id").GetString()}/retry", new { nonce = "nonce-retry-2" });
        Assert.Equal(409, again.Status);
        Assert.Equal(2, (await Runs(app, id)).Count);
    }

    [Fact]
    public async Task Browser_closure_after_accept_does_not_lose_the_run()
    {
        using var caller = new CancellationTokenSource();
        var model = new FakeModel(async (ctx, ct) =>
        {
            caller.Cancel(); // user closes the tab mid-processing
            await Task.Delay(50, ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        var app = TestApp.With(model);
        var id = await app.CreateRequest();

        try { await app.Analyze(id, "nonce-close-1", ct: caller.Token); }
        catch (OperationCanceledException) { /* the HTTP response may be lost; the run must not be */ }

        var run = Assert.Single(await Runs(app, id));
        Assert.Equal(ProcessingStatus.Completed, run.ProcessingStatus);
        Assert.NotNull(run.Result);
    }

    [Fact]
    public async Task Operator_status_wins_over_late_completion()
    {
        TestApp? app = null;
        var model = new FakeModel(async (ctx, ct) =>
        {
            var run = (await app!.Stores.Runs.GetAsync(TestApp.Alpha, ctx.ReviewRunId, ct))!;
            await app.Stores.Runs.ReplaceAsync(run.Value with { ProcessingStatus = ProcessingStatus.Failed, ErrorCategory = ErrorCategory.Timeout }, run.ETag, ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        app = TestApp.With(model);
        var id = await app.CreateRequest();
        await app.Analyze(id);

        var run = Assert.Single(await Runs(app, id));
        Assert.Equal(ProcessingStatus.Failed, run.ProcessingStatus);
        Assert.Null(run.Result);
    }

    [Fact]
    public async Task Reviewer_failure_is_failed_with_safe_category_only()
    {
        var app = TestApp.With(new FakeModel((_, _) => throw new InvalidOperationException("connection string leaked?")));
        var id = await app.CreateRequest();
        var a = await app.Analyze(id);
        var run = await app.Run(a.Json.GetProperty("runId").GetString()!);

        Assert.Equal("Failed", run.Value.GetProperty("processingStatus").GetString());
        Assert.Equal("DependencyUnavailable", run.Value.GetProperty("errorCategory").GetString());
        Assert.DoesNotContain("leaked", run.Body);
    }

    [Fact]
    public async Task Malformed_result_fails_run()
    {
        var app = TestApp.With(new FakeModel((_, _) => throw new ReviewFailedException(ErrorCategory.MalformedResult, "bad output")));
        var id = await app.CreateRequest();
        var run = await app.Run((await app.Analyze(id)).Json.GetProperty("runId").GetString()!);
        Assert.Equal("Failed", run.Value.GetProperty("processingStatus").GetString());
        Assert.Equal("MalformedResult", run.Value.GetProperty("errorCategory").GetString());
    }

    [Fact]
    public async Task Oversized_reviewer_text_is_capped_not_trusted()
    {
        var app = TestApp.With(FakeModel.Returning(new ModelReview { Summary = new string('x', 5000), ClarificationQuestions = [new string('q', 900)] }));
        var id = await app.CreateRequest();
        var run = (await app.Run((await app.Analyze(id)).Json.GetProperty("runId").GetString()!)).Value;
        var result = run.GetProperty("result");
        Assert.True(result.GetProperty("summary").GetString()!.Length <= 1000);
        Assert.All(result.GetProperty("clarificationQuestions").EnumerateArray(), q => Assert.True(q.GetString()!.Length <= 300));
    }

    [Fact]
    public async Task Tampered_snapshot_fails_integrity_check()
    {
        var app = TestApp.With();
        var id = await app.CreateRequest();
        var req = (await app.Stores.Requests.GetAsync(TestApp.Alpha, id))!.Value;
        var snapshot = SnapshotBuilder.Build(req, app.Clock.UtcNow);
        await app.Stores.Runs.CreateAsync(new ReviewRun
        {
            Id = $"{id}.r1.a0", ProjectId = TestApp.Alpha, RequestId = id, RequestRevision = 1,
            Snapshot = snapshot with { Role = "SAMPLE Admin" }, SnapshotHash = SnapshotBuilder.Hash(snapshot),
            InitiatedBy = TestApp.Alice, ProcessingStatus = ProcessingStatus.Queued, ReviewerProvider = "RuleOnly", ReviewerVersion = "1.0.0",
            CorrelationId = "c", QueuedAt = app.Clock.UtcNow, IdempotencyKey = $"{id}:1:manual",
        });
        var (processor, _) = PilotApp.BuildProcessing(app.Stores, new PilotSettings(), app.Clock, NullLoggerFactory.Instance, new RuleOnlyReviewModel());
        await processor.ProcessAsync(TestApp.Alpha, $"{id}.r1.a0", "c");

        var run = (await app.Stores.Runs.GetAsync(TestApp.Alpha, $"{id}.r1.a0"))!.Value;
        Assert.Equal(ProcessingStatus.Failed, run.ProcessingStatus);
        Assert.Equal(ErrorCategory.Internal, run.ErrorCategory);
    }

    [Fact]
    public async Task Operator_recovers_stuck_runs_only()
    {
        var app = TestApp.With();
        var id = await app.CreateRequest();
        var req = (await app.Stores.Requests.GetAsync(TestApp.Alpha, id))!.Value;
        var snap = SnapshotBuilder.Build(req, app.Clock.UtcNow);
        ReviewRun Make(string runId, DateTimeOffset started) => new()
        {
            Id = runId, ProjectId = TestApp.Alpha, RequestId = id, RequestRevision = 1, Snapshot = snap, SnapshotHash = SnapshotBuilder.Hash(snap),
            InitiatedBy = TestApp.Alice, ProcessingStatus = ProcessingStatus.Running, ReviewerProvider = "RuleOnly", ReviewerVersion = "1.0.0",
            CorrelationId = "c", QueuedAt = started, StartedAt = started, IdempotencyKey = $"{runId}:k",
        };
        await app.Stores.Runs.CreateAsync(Make("stuck-run", app.Clock.UtcNow.AddMinutes(-10)));
        await app.Stores.Runs.CreateAsync(Make("fresh-run", app.Clock.UtcNow.AddMinutes(-1)));

        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/operator/recover")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Alpha}/operator/recover")).Status);

        var r = await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/operator/recover");
        Assert.Equal(200, r.Status);
        Assert.Equal(["stuck-run"], r.Json.GetProperty("recovered").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(ProcessingStatus.Failed, (await app.Stores.Runs.GetAsync(TestApp.Alpha, "stuck-run"))!.Value.ProcessingStatus);
        Assert.Equal(ErrorCategory.Timeout, (await app.Stores.Runs.GetAsync(TestApp.Alpha, "stuck-run"))!.Value.ErrorCategory);
        Assert.Equal(ProcessingStatus.Running, (await app.Stores.Runs.GetAsync(TestApp.Alpha, "fresh-run"))!.Value.ProcessingStatus);
    }

    [Fact]
    public async Task Invalid_nonce_is_rejected()
    {
        var app = TestApp.With();
        var id = await app.CreateRequest();
        Assert.Equal(400, (await app.Analyze(id, "x")).Status);
        Assert.Equal(400, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests/{id}/analyze", new { })).Status);
    }

    [Fact]
    public async Task Queue_dispatch_is_refused_without_paid_flag_and_works_with_mocks()
    {
        Assert.Throws<InvalidOperationException>(() => TestApp.With(settings: new PilotSettings { Dispatcher = "Queue" }));

        var queue = new FakeQueue();
        var settings = new PilotSettings { Dispatcher = "Queue", AllowPaidServices = true };
        var app = TestApp.With(settings: settings, queue: queue);
        var id = await app.CreateRequest();
        var a = await app.Analyze(id);
        Assert.Equal("Queued", a.Json.GetProperty("processingStatus").GetString());

        var (processor, _) = PilotApp.BuildProcessing(app.Stores, settings, app.Clock, NullLoggerFactory.Instance, new RuleOnlyReviewModel());
        var handler = new QueueMessageHandler(processor);
        Assert.True(await handler.HandleAsync(Assert.Single(queue.Messages)));
        Assert.False(await handler.HandleAsync("{not json"));

        var run = await app.Run(a.Json.GetProperty("runId").GetString()!);
        Assert.Equal("Completed", run.Value.GetProperty("processingStatus").GetString());
    }

    private sealed class FakeQueue : IQueueClient
    {
        public List<string> Messages { get; } = [];

        public Task SendAsync(string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
