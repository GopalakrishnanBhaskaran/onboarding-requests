using PmoReview.Core.Http;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

/// <summary>
/// F114 (re-check 6): an unexpected API error ("Something went wrong") is findable again without Application Insights:
/// one issue per route and error type on the project's Issues list, with the last references and the code location, and
/// nothing from the request or the error message.
/// </summary>
public class ApiFailureLogTests
{
    private const string Secretish = "SAMPLE-PERSONAL sample.e1001@sample-corp.invalid";

    private static TestApp WithBrokenRoute()
    {
        var app = TestApp.Create();
        app.Host.Map("POST", "projects/{projectId}/sample-broken/{enterpriseId:upn}", _ => throw new InvalidOperationException(Secretish));
        app.Host.Map("GET", "sample-broken-everywhere", _ => throw new FormatException(Secretish));
        return app;
    }

    private static string Reference(Resp r) => r.Json.GetProperty("error").GetProperty("correlationId").GetString()!;

    [Fact]
    public async Task An_unexpected_error_is_on_the_projects_issues_list_with_route_type_code_location_and_reference_only()
    {
        var app = WithBrokenRoute();
        var r = await app.Call(TestApp.Paul, "POST", $"projects/{TestApp.Alpha}/sample-broken/{Uri.EscapeDataString("sample.e1001@sample-corp.invalid")}",
            new { justification = Secretish });
        Assert.Equal(500, r.Status);
        Assert.Equal(("internal_error", ApiHost.InternalErrorMessage), (r.Json.GetProperty("error").GetProperty("code").GetString(), r.Json.GetProperty("error").GetProperty("message").GetString()));
        Assert.DoesNotContain("SAMPLE-PERSONAL", r.Body);

        var issue = Assert.Single(await app.Issues(null), i => i.Step.StartsWith("api:", StringComparison.Ordinal));
        Assert.Equal((IssueKind.NeedsDeveloper, IssueSeverity.Warning, IssueStatus.Open, 1), (issue.Kind, issue.Severity, issue.Status, issue.Occurrences));
        Assert.Equal("api:POST projects/{projectId}/sample-broken/{enterpriseId:upn}:InvalidOperationException", issue.Step); // the template, never the path
        Assert.Contains(Reference(r), issue.Summary);
        Assert.Contains($"References, latest first: {Reference(r)}.", issue.Detail);
        Assert.Contains("PmoReview.Tests.Security.ApiFailureLogTests", issue.Detail); // where it was thrown
        Assert.Contains("ApiFailureLogTests.cs:", issue.Detail);                    // file and line, no build path
        Assert.DoesNotContain("/home/", issue.Detail);
        foreach (var text in new[] { issue.Summary, issue.Detail! })
        {
            Assert.DoesNotContain("SAMPLE-PERSONAL", text);       // never the error message or the body
            Assert.DoesNotContain("sample.e1001", text);          // never the path value
            Assert.DoesNotContain(TestApp.Paul, text);            // never the caller
        }
    }

    [Fact]
    public async Task Repeats_count_on_one_issue_and_keep_the_last_five_references_latest_first()
    {
        var app = WithBrokenRoute();
        var refs = new List<string>();
        for (var i = 0; i < 7; i++)
            refs.Add(Reference(await app.Call(TestApp.Paul, "POST", $"projects/{TestApp.Alpha}/sample-broken/{Uri.EscapeDataString("sample.x@sample.invalid")}", new { })));
        var issue = Assert.Single(await app.Issues(null), i => i.Step.StartsWith("api:", StringComparison.Ordinal));
        Assert.Equal(7, issue.Occurrences);
        Assert.Contains($"References, latest first: {string.Join(", ", Enumerable.Reverse(refs).Take(5))}.", issue.Detail);
    }

    [Fact]
    public async Task An_error_is_recorded_only_in_projects_the_caller_has_a_role_in()
    {
        var app = WithBrokenRoute();
        // Bob has no role in Alpha: nothing goes on Alpha's list; his own project gets it.
        Assert.Equal(500, (await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Alpha}/sample-broken/{Uri.EscapeDataString("sample.x@sample.invalid")}", new { })).Status);
        Assert.DoesNotContain(await app.Issues(null, TestApp.Alpha), i => i.Step.StartsWith("api:", StringComparison.Ordinal));
        Assert.Single(await app.Issues(null, TestApp.Beta), i => i.Step.StartsWith("api:", StringComparison.Ordinal));

        // A route with no project (like /me): each of the caller's projects; a caller with none: the platform list (workflow_diag.sh).
        Assert.Equal(500, (await app.Call(TestApp.Stranger, "GET", "sample-broken-everywhere")).Status);
        var platform = (await app.Stores.Issues.ListAsync(ApiFailureLog.PlatformPartition)).Select(v => v.Value).ToList();
        Assert.Contains(platform, i => i.Step == "api:GET sample-broken-everywhere:FormatException");
    }

    [Fact]
    public async Task A_failure_to_record_never_changes_the_answer()
    {
        var app = TestApp.Create();
        var log = new ApiFailureLog(app.Stores with { Issues = new BrokenIssues() }, app.Worker.Issues);
        var host = new ApiHost(new PmoReview.Core.Security.AccessService(app.Stores), ["aad"], Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiHost>.Instance, log);
        host.Map("GET", "sample-broken", _ => throw new InvalidOperationException("SAMPLE"));
        var r = await host.HandleAsync(new ApiRequest("GET", "sample-broken",
            new Dictionary<string, string> { [PmoReview.Core.Security.ClientPrincipalParser.HeaderName] = TestApp.Principal(TestApp.Paul) }, null, "sample-cid"), CancellationToken.None);
        Assert.Equal(500, r.Status);
    }

    [Fact]
    public void The_code_location_drops_parameter_names_and_paths()
    {
        try
        {
            Thrower.Throw("SAMPLE value");
        }
        catch (Exception e)
        {
            var d = ApiFailureLog.Describe(new ApplicationException("outer", e));
            Assert.StartsWith("ApplicationException (inner InvalidOperationException)", d);
            var inner = ApiFailureLog.Describe(e);
            Assert.Contains("PmoReview.Tests.Security.Thrower.Throw (ApiFailureLogTests.cs:", inner);
            Assert.DoesNotContain("String", inner);
            Assert.DoesNotContain("SAMPLE value", inner);
        }
    }

    private sealed class BrokenIssues : PmoReview.Core.Storage.IDocumentStore<Issue>
    {
        private static Exception E => new InvalidOperationException("SAMPLE store down");
        public Task<PmoReview.Core.Storage.Versioned<Issue>?> GetAsync(string partitionKey, string id, CancellationToken ct = default) => throw E;
        public Task<IReadOnlyList<PmoReview.Core.Storage.Versioned<Issue>>> ListAsync(string partitionKey, CancellationToken ct = default) => throw E;
        public Task<IReadOnlyList<PmoReview.Core.Storage.Versioned<Issue>>> ListAllAsync(CancellationToken ct = default) => throw E;
        public Task<PmoReview.Core.Storage.Versioned<Issue>> CreateAsync(Issue doc, CancellationToken ct = default) => throw E;
        public Task<PmoReview.Core.Storage.Versioned<Issue>> ReplaceAsync(Issue doc, string etag, CancellationToken ct = default) => throw E;
    }
}

internal static class Thrower
{
    public static void Throw(string value) => throw new InvalidOperationException(value);
}
