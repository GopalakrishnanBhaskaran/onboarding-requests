using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

/// <summary>Phase 1 exit gate: two users in different projects cannot read, review or tamper with each other's data.</summary>
public class CrossProjectTests
{
    private static async Task<(TestApp App, string Id, string ETag)> AliceRequest()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests",
            new { requestType = "NewJoiner", role = "SAMPLE Data Engineer", environment = "Dev" });
        Assert.Equal(201, r.Status);
        return (app, r.Value.GetProperty("id").GetString()!, r.ETag);
    }

    [Fact]
    public async Task Other_project_user_cannot_read_by_id()
    {
        var (app, id, _) = await AliceRequest();
        var r = await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Alpha}/requests/{id}");
        Assert.Equal(404, r.Status);
        Assert.DoesNotContain("SAMPLE Data Engineer", r.Body);
    }

    [Fact]
    public async Task Other_project_user_cannot_list()
    {
        var (app, _, _) = await AliceRequest();
        var r = await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Alpha}/requests");
        Assert.Equal(404, r.Status);
    }

    [Fact]
    public async Task Request_id_under_own_project_route_does_not_reach_other_partition()
    {
        var (app, id, _) = await AliceRequest();
        // Bob uses his own project in the route with Alice's request id.
        var r = await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Beta}/requests/{id}");
        Assert.Equal(404, r.Status);
    }

    [Fact]
    public async Task Other_project_user_cannot_update()
    {
        var (app, id, etag) = await AliceRequest();
        var r = await app.Call(TestApp.Bob, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", new { role = "hijack" }, ifMatch: etag);
        Assert.Equal(404, r.Status);

        var check = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests/{id}");
        Assert.Equal("SAMPLE Data Engineer", check.Value.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Other_project_user_cannot_create_in_foreign_project()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Alpha}/requests", new { role = "x" });
        Assert.Equal(404, r.Status);
    }

    [Fact]
    public async Task Forged_body_fields_cannot_move_or_tamper_a_request()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Beta}/requests", new
        {
            projectId = TestApp.Alpha,
            id = "forged-id",
            revision = 99,
            createdBy = TestApp.Alice,
            processingStatus = "Completed",
            readiness = "ReadyForHumanReview",
            result = new { readiness = "ReadyForHumanReview" },
            role = "SAMPLE Tester",
        });
        Assert.Equal(201, r.Status);
        Assert.Equal(TestApp.Beta, r.Value.GetProperty("projectId").GetString());
        Assert.NotEqual("forged-id", r.Value.GetProperty("id").GetString());
        Assert.Equal(1, r.Value.GetProperty("revision").GetInt32());
        Assert.False(r.Value.TryGetProperty("result", out _));

        var alphaList = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests");
        Assert.Equal(0, alphaList.Json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Other_project_user_cannot_read_or_change_reference_data()
    {
        var app = TestApp.Create();
        Assert.Equal(404, (await app.Call(TestApp.Bella, "GET", $"projects/{TestApp.Alpha}/rules")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bella, "POST", $"projects/{TestApp.Alpha}/rules", new
        {
            ruleId = "X", requestType = "NewJoiner", field = "role", rule = "Required",
            requirementText = "SAMPLE", effectiveFrom = "2026-01-01", sourceReference = "SAMPLE",
        })).Status);
        var rules = await app.Call(TestApp.Bella, "GET", $"projects/{TestApp.Beta}/rules");
        Assert.All(rules.Json.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(TestApp.Beta, i.GetProperty("value").GetProperty("projectId").GetString()));
    }

    [Theory]
    [InlineData("projects/..%2FSAMPLE-ALPHA/requests")]
    [InlineData("projects/SAMPLE-ALPHA%27%20OR%201%3D1/requests")]
    [InlineData("projects/*/requests")]
    public async Task Malformed_route_ids_are_rejected(string path)
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Alice, "GET", path);
        Assert.Equal(404, r.Status);
    }
}
