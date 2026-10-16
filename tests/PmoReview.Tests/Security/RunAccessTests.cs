using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

/// <summary>Review runs and reviewer feedback: cross-project isolation, role checks, no tampering.</summary>
public class RunAccessTests
{
    private static async Task<(TestApp App, string RequestId, string RunId)> CompletedRun(object? body = null)
    {
        var app = TestApp.With();
        var id = await app.CreateRequest(body: body);
        var a = await app.Analyze(id);
        return (app, id, a.Json.GetProperty("runId").GetString()!);
    }

    [Fact]
    public async Task Other_project_users_cannot_analyze_read_or_review()
    {
        var (app, id, runId) = await CompletedRun();
        var p = $"projects/{TestApp.Alpha}";

        Assert.Equal(404, (await app.Analyze(id, user: TestApp.Bob)).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"{p}/runs/{runId}")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"{p}/requests/{id}/runs")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "POST", $"{p}/runs/{runId}/retry", new { nonce = "nonce-bob-1" })).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bella, "POST", $"{p}/runs/{runId}/decisions", new { outcome = "Accepted" })).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bella, "GET", $"{p}/runs/{runId}/decisions")).Status);

        // Own-project route with a foreign run id finds nothing.
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Beta}/runs/{runId}")).Status);
    }

    [Fact]
    public async Task Only_reviewer_records_feedback_once_with_server_identity()
    {
        var (app, _, runId) = await CompletedRun();
        var path = $"projects/{TestApp.Alpha}/runs/{runId}/decisions";

        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", path, new { outcome = "Accepted" })).Status);
        Assert.Equal(400, (await app.Call(TestApp.Rita, "POST", path, new { outcome = "Corrected" })).Status); // comment required

        var ok = await app.Call(TestApp.Rita, "POST", path, new
        {
            outcome = "Accepted", reviewer = TestApp.Alice, readiness = "ReadyForHumanReview",
            result = new { readiness = "ReadyForHumanReview" }, processingStatus = "Completed",
        });
        Assert.Equal(201, ok.Status);
        Assert.Equal(TestApp.Rita, ok.Value.GetProperty("reviewer").GetString());

        Assert.Equal(409, (await app.Call(TestApp.Rita, "POST", path, new { outcome = "Accepted" })).Status);
        Assert.Single((await app.Call(TestApp.Alice, "GET", path)).Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task No_api_can_write_results_or_status()
    {
        var (app, _, runId) = await CompletedRun();
        var before = (await app.Run(runId)).Body;
        var p = $"projects/{TestApp.Alpha}/runs/{runId}";

        Assert.Equal(405, (await app.Call(TestApp.Alice, "PUT", p, new { processingStatus = "Completed", readiness = "ReadyForHumanReview" })).Status);
        Assert.Equal(405, (await app.Call(TestApp.Oscar, "PUT", p, new { result = new { } })).Status);
        Assert.Equal(404, (await app.Call(TestApp.Rita, "POST", $"{p}/result", new { readiness = "ReadyForHumanReview" })).Status);
        await app.Call(TestApp.Rita, "POST", $"{p}/decisions", new { outcome = "Escalated", comment = "SAMPLE", readiness = "ReadyForHumanReview" });

        var after = await app.Run(runId);
        Assert.Equal(before, after.Body);
    }

    [Fact]
    public async Task Role_checks_on_runs()
    {
        var (app, id, runId) = await CompletedRun();
        Assert.Equal(403, (await app.Analyze(id, "nonce-rita-1", user: TestApp.Rita)).Status);
        Assert.Equal(200, (await app.Run(runId, user: TestApp.Rita)).Status);
        Assert.Equal(200, (await app.Run(runId, user: TestApp.Oscar)).Status);
        Assert.Equal(403, (await app.Run(runId, user: TestApp.Mark)).Status);
        Assert.Equal(403, (await app.Call(TestApp.Oscar, "GET", $"projects/{TestApp.Alpha}/requests/{id}")).Status);
    }

    [Fact]
    public async Task Feedback_needs_a_completed_run()
    {
        var app = TestApp.With(new FakeModel((_, _) => throw new InvalidOperationException()));
        var id = await app.CreateRequest();
        var runId = (await app.Analyze(id)).Json.GetProperty("runId").GetString();
        var r = await app.Call(TestApp.Rita, "POST", $"projects/{TestApp.Alpha}/runs/{runId}/decisions", new { outcome = "Accepted" });
        Assert.Equal(409, r.Status);
    }

    [Fact]
    public async Task Attention_queue_is_project_scoped()
    {
        var (app, _, _) = await CompletedRun(new
        {
            requestType = "NewJoiner", role = "SAMPLE Data Engineer", environment = "Prod", manager = "SAMPLE Manager One",
            justification = "SAMPLE: production support rota for ingestion pipelines.", startDate = "2026-11-02", endDate = "2027-03-31",
        });
        var bobRequest = await app.CreateRequest(TestApp.Bob, TestApp.Beta, new { requestType = "NewJoiner" });
        await app.Analyze(bobRequest, user: TestApp.Bob, project: TestApp.Beta);

        var rita = await app.Call(TestApp.Rita, "GET", "attention");
        var ritaItems = rita.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(ritaItems);
        Assert.All(ritaItems, i => Assert.Equal(TestApp.Alpha, i.GetProperty("projectId").GetString()));

        var bella = await app.Call(TestApp.Bella, "GET", "attention");
        Assert.All(bella.Json.GetProperty("items").EnumerateArray(), i => Assert.Equal(TestApp.Beta, i.GetProperty("projectId").GetString()));

        Assert.Empty((await app.Call(TestApp.Alice, "GET", "attention")).Json.GetProperty("items").EnumerateArray());
    }
}
