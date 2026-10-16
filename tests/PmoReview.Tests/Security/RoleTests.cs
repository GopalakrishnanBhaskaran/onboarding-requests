using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

/// <summary>Server-side role checks within one project.</summary>
public class RoleTests
{
    private static readonly object Rule = new
    {
        ruleId = "A-NEW", requestType = "NewJoiner", field = "manager", rule = "Required",
        requirementText = "SAMPLE: manager required", effectiveFrom = "2026-01-01", sourceReference = "SAMPLE-DOC",
    };

    [Fact]
    public async Task Reviewer_can_read_but_not_create_requests()
    {
        var app = TestApp.Create();
        Assert.Equal(200, (await app.Call(TestApp.Rita, "GET", $"projects/{TestApp.Alpha}/requests")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Rita, "POST", $"projects/{TestApp.Alpha}/requests", new { role = "x" })).Status);
    }

    [Fact]
    public async Task Maintainer_cannot_read_requests()
    {
        var app = TestApp.Create();
        Assert.Equal(403, (await app.Call(TestApp.Mark, "GET", $"projects/{TestApp.Alpha}/requests")).Status);
    }

    [Fact]
    public async Task Only_maintainer_can_create_and_publish_rules()
    {
        var app = TestApp.Create();
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/rules", Rule)).Status);

        var created = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/rules", Rule);
        Assert.Equal(201, created.Status);
        Assert.Equal("Draft", created.Value.GetProperty("publicationStatus").GetString());
        Assert.True(created.Value.GetProperty("isSample").GetBoolean());
        var id = created.Value.GetProperty("id").GetString();

        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/rules/{id}/status",
            new { status = "Published" }, ifMatch: created.ETag)).Status);

        var published = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/rules/{id}/status",
            new { status = "Published" }, ifMatch: created.ETag);
        Assert.Equal(200, published.Status);

        // Published records are immutable; a second publish is rejected.
        var again = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/rules/{id}/status",
            new { status = "Published" }, ifMatch: published.ETag);
        Assert.Equal(400, again.Status);
    }

    [Fact]
    public async Task New_rule_version_is_created_not_overwritten()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/rules", new
        {
            ruleId = "A-REQ-ROLE", requestType = "NewJoiner", field = "role", rule = "Required",
            requirementText = "SAMPLE v2", effectiveFrom = "2026-11-01", sourceReference = "SAMPLE-DOC v2",
        });
        Assert.Equal(201, r.Status);
        Assert.Equal(2, r.Value.GetProperty("version").GetInt32());
        Assert.Equal("A-REQ-ROLE:v2", r.Value.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Rule_with_bad_parameter_is_rejected()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/rules", new
        {
            ruleId = "A-BAD", requestType = "NewJoiner", field = "justification", rule = "MinLength", parameter = "abc",
            requirementText = "SAMPLE", effectiveFrom = "2026-01-01", sourceReference = "SAMPLE",
        });
        Assert.Equal(400, r.Status);
    }
}
