using PmoReview.Adapters;
using PmoReview.Adapters.Review;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Run lifecycle: one run per attempt, truthful statuses, snapshot integrity, recovery.</summary>
public class ProcessingTests
{
    private static async Task<IReadOnlyList<ReviewRun>> Runs(TestApp app, string requestId) =>
        (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Select(r => r.Value).Where(r => r.RequestId == requestId).OrderBy(r => r.QueuedAt).ThenBy(r => r.Id).ToList();

    [Fact]
    public async Task Submit_runs_the_review_inline_with_snapshot_pinned_rows_and_fingerprint()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId();
        var run = Assert.Single(await Runs(app, id));
        Assert.Equal(ProcessingStatus.Completed, run.ProcessingStatus);
        Assert.Equal(ReviewOutcome.Pending, run.Outcome); // no Entra reader in the API
        Assert.Equal($"{id}.r1.a0", run.Id);
        Assert.Equal("submit", run.Trigger);
        Assert.Matches("^[0-9a-f]{64}$", run.SnapshotHash);
        Assert.Matches("^[0-9a-f]{64}$", run.SheetFingerprint);
        Assert.NotEmpty(run.MappingRows);
        Assert.Equal("RuleOnly", run.ReviewerProvider);
        Assert.Null(run.PromptVersion);
        Assert.Equal(WorkflowState.AgentActor, run.InitiatedBy);
    }

    [Fact]
    public async Task Concurrent_agent_steps_create_one_run_per_attempt()
    {
        var model = new FakeModel(async (ctx, ct) =>
        {
            await Task.Delay(100, ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        var app = TestApp.With(model, apiDirectory: FakeDirectory.SeededFromSample());
        var id = await app.SubmitId();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => app.Api.Agent.AdvanceAsync(TestApp.Alpha, id, "c", CancellationToken.None))));
        Assert.Single(await Runs(app, id));
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    [Fact]
    public async Task Edit_during_processing_marks_the_run_superseded()
    {
        TestApp? app = null;
        string? requestId = null;
        var edited = false;
        var model = new FakeModel(async (ctx, ct) =>
        {
            if (!edited && requestId is not null)
            {
                edited = true;
                var req = (await app!.Stores.Requests.GetAsync(TestApp.Alpha, requestId, ct))!;
                await app.Stores.Requests.ReplaceAsync(req.Value with { Revision = req.Value.Revision + 1 }, req.ETag, ct);
            }
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        app = TestApp.With(model, apiDirectory: FakeDirectory.SeededFromSample());
        requestId = await app.SubmitId(); // first review runs before requestId is known
        var request = await app.Request(requestId);
        var started = await app.Api.Runs.StartAsync(request, "test", "tester", "c");
        Assert.Equal(ProcessingStatus.Superseded, started.ProcessingStatus);
        var run = (await app.Stores.Runs.GetAsync(TestApp.Alpha, started.RunId))!.Value;
        Assert.Equal(request.Revision + 1, run.SupersededByRevision);
    }

    [Fact]
    public async Task Timeout_fails_the_run_with_a_category()
    {
        var model = new FakeModel(async (ctx, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        var app = TestApp.With(model, new PilotSettings { ProcessingBudgetSeconds = 1 });
        var id = await app.SubmitId();
        var run = (await Runs(app, id))[0];
        Assert.Equal(ProcessingStatus.Failed, run.ProcessingStatus);
        Assert.Equal(ErrorCategory.Timeout, run.ErrorCategory);
        Assert.Null(run.Result);
    }

    [Fact]
    public async Task Tampered_snapshot_is_never_reviewed()
    {
        var app = TestApp.With(settings: new PilotSettings { Dispatcher = "Queue", AllowPaidServices = true }, queue: new ListQueue());
        var id = await app.SubmitId();
        var run = (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Single(r => r.Value.RequestId == id);
        await app.Stores.Runs.ReplaceAsync(run.Value with { Snapshot = run.Value.Snapshot with { AccessLevel = "Admin" } }, run.ETag);
        await app.Api.Processor.ProcessAsync(TestApp.Alpha, run.Value.Id, "c");
        var after = (await app.Stores.Runs.GetAsync(TestApp.Alpha, run.Value.Id))!.Value;
        Assert.Equal(ProcessingStatus.Failed, after.ProcessingStatus);
        Assert.Equal(ErrorCategory.Internal, after.ErrorCategory);
    }

    [Fact]
    public async Task Operator_recovers_runs_stuck_past_the_threshold()
    {
        var app = TestApp.With(settings: new PilotSettings { Dispatcher = "Queue", AllowPaidServices = true }, queue: new ListQueue());
        var id = await app.SubmitId();
        Assert.Equal(200, (await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/operator/recover")).Status);
        Assert.Equal(ProcessingStatus.Queued, (await Runs(app, id))[0].ProcessingStatus); // not stuck yet
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        var r = await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/operator/recover");
        Assert.Single(r.Json.GetProperty("recovered").EnumerateArray());
        Assert.Equal(ErrorCategory.Timeout, (await Runs(app, id))[0].ErrorCategory);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/operator/recover")).Status);
    }

    private sealed class ListQueue : PmoReview.Adapters.Dispatch.IQueueClient
    {
        public List<string> Messages { get; } = [];
        public Task SendAsync(string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
