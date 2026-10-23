using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Adapters.Entra;
using PmoReview.Adapters.Outbound;
using PmoReview.Adapters.Rbac;
using PmoReview.Core.Directory;
using PmoReview.Core.Workflow;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>HTTP stand-in: routes by method + URL prefix, records every call.</summary>
public sealed class RouteHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Prefix, Func<HttpResponseMessage> Reply, int[] Left)> _routes = [];
    public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = [];

    /// <param name="times">How many calls this route answers before the next matching route takes over.</param>
    public RouteHandler On(HttpMethod method, string prefix, string json, HttpStatusCode code = HttpStatusCode.OK, int times = int.MaxValue)
    {
        _routes.Add((method, prefix, () => new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, [times]));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        Calls.Add((request.Method, url, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        foreach (var (m, p, reply, left) in _routes)
        {
            if (m != request.Method || !url.StartsWith(p, StringComparison.Ordinal) || left[0] <= 0) continue;
            left[0]--;
            return reply();
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }
}

public sealed class StaticCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("sample-token", DateTimeOffset.MaxValue);
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

public class GraphDirectoryReaderTests
{
    private const string G = "https://graph.microsoft.com/v1.0/";
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string GroupId = "22222222-2222-2222-2222-222222222222";

    private static (GraphDirectoryReader Reader, RouteHandler Http) Reader(Action<RouteHandler> routes)
    {
        var h = new RouteHandler();
        routes(h);
        return (new GraphDirectoryReader(new HttpClient(h), new StaticCredential()), h);
    }

    [Fact]
    public async Task Reads_user_group_membership_methods_signins_and_manager_with_get_and_one_read_action_only()
    {
        var (reader, http) = Reader(h => h
            .On(HttpMethod.Get, $"{G}users?$filter=userPrincipalName eq 'sample.e1001@sample.invalid'", $$"""{"value":[{"id":"{{UserId}}","userPrincipalName":"sample.e1001@sample.invalid","userType":"Member","accountEnabled":true}]}""")
            .On(HttpMethod.Get, $"{G}groups?$filter=displayName eq 'SAMPLE-GRP-A'", $$"""{"value":[{"id":"{{GroupId}}","displayName":"SAMPLE-GRP-A","securityEnabled":true}]}""")
            .On(HttpMethod.Post, $"{G}users/{UserId}/checkMemberGroups", $$"""{"value":["{{GroupId}}"]}""")
            .On(HttpMethod.Get, $"{G}users/{UserId}/authentication/methods", """
                {"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod","id":"p"},
                          {"@odata.type":"#microsoft.graph.temporaryAccessPassAuthenticationMethod","id":"t","createdDateTime":"2026-10-17T09:00:00Z","startDateTime":"2026-10-17T09:00:00Z","lifetimeInMinutes":60,"isUsableOnce":true,"isUsable":true,"methodUsabilityReason":"EnabledByPolicy","temporaryAccessPass":null}]}
                """)
            .On(HttpMethod.Get, $"{G}auditLogs/signIns", """{"value":[{"createdDateTime":"2026-10-17T10:00:00Z"}]}""")
            .On(HttpMethod.Get, $"{G}users/{UserId}/manager", """{"mail":"sample.manager@sample.invalid"}"""));

        var user = await reader.FindUserAsync("sample.e1001@sample.invalid", CancellationToken.None);
        Assert.Equal(UserId, user!.ObjectId);
        var group = Assert.Single(await reader.FindGroupsAsync("SAMPLE-GRP-A", CancellationToken.None));
        Assert.True(group.SecurityEnabled);
        Assert.True(await reader.IsMemberAsync(GroupId, UserId, CancellationToken.None));
        var methods = await reader.GetAuthMethodsAsync(UserId, CancellationToken.None);
        Assert.Equal(["password", "temporaryAccessPass"], methods.MethodTypes);
        Assert.Equal(60, methods.Tap!.LifetimeMinutes);
        Assert.True(methods.Tap.IsUsableOnce);
        Assert.False(methods.HasPermanentMethod);
        var signIn = await reader.LastSuccessfulSignInAsync(UserId, new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(10, signIn!.At.Hour);
        Assert.Equal("sample.manager@sample.invalid", await reader.GetManagerAsync("sample.e1001@sample.invalid", CancellationToken.None));

        Assert.All(http.Calls, c => Assert.True(c.Method == HttpMethod.Get || c.Url.EndsWith("/checkMemberGroups", StringComparison.Ordinal), c.Url));
        Assert.All(http.Calls, c => Assert.StartsWith(G, c.Url));
        Assert.Contains(http.Calls, c => c.Url.Contains("status/errorCode eq 0", StringComparison.Ordinal) && c.Url.Contains("createdDateTime ge 2026-10-17T09:00:00Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manager_lookup_refused_by_graph_is_graph_forbidden()
    {
        // Learn (17 Oct 2026) lists application permissions for GET /users/{id}/manager as "Not supported" (F49):
        // a 403 must surface as GraphForbidden so the agent falls back to the configured contact.
        var (reader, _) = Reader(h => h
            .On(HttpMethod.Get, $"{G}users?$filter=userPrincipalName eq 'sample.e1001@sample.invalid'", $$"""{"value":[{"id":"{{UserId}}","userPrincipalName":"sample.e1001@sample.invalid"}]}""")
            .On(HttpMethod.Get, $"{G}users/{UserId}/manager", """{"error":{"code":"Authorization_RequestDenied"}}""", HttpStatusCode.Forbidden));

        var e = await Assert.ThrowsAsync<DirectoryReadException>(() => reader.GetManagerAsync("sample.e1001@sample.invalid", CancellationToken.None));
        Assert.Equal("GraphForbidden", e.Category);
    }

    [Fact]
    public async Task Missing_user_is_null_and_quotes_are_escaped()
    {
        var (reader, http) = Reader(h => h.On(HttpMethod.Get, $"{G}users?", """{"value":[]}"""));
        Assert.Null(await reader.FindUserAsync("o'brien@sample.invalid", CancellationToken.None));
        Assert.Contains("'o''brien@sample.invalid'", http.Calls.Single().Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GraphForbidden")]
    [InlineData(HttpStatusCode.BadRequest, "GraphError")]
    public async Task Errors_become_categories(HttpStatusCode code, string category)
    {
        var (reader, _) = Reader(h => h.On(HttpMethod.Get, G, "{}", code));
        var e = await Assert.ThrowsAsync<DirectoryReadException>(() => reader.FindGroupsAsync("x", CancellationToken.None));
        Assert.Equal(category, e.Category);
    }

    [Fact]
    public async Task Throttling_is_retried_once()
    {
        var h = new ThrottleThenOk();
        var reader = new GraphDirectoryReader(new HttpClient(h), new StaticCredential());
        Assert.Empty(await reader.FindGroupsAsync("x", CancellationToken.None));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public void Reader_has_no_write_code_path()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "src", "adapters", "PmoReview.Adapters", "Entra", "GraphDirectoryReader.cs"));
        Assert.DoesNotContain("HttpMethod.Patch", source);
        Assert.DoesNotContain("HttpMethod.Delete", source);
        Assert.DoesNotContain("HttpMethod.Put", source);
        Assert.Contains("WriteRefused", source);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, "HttpMethod\\.Post,"));
    }

    internal static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "PmoReview.slnx"))) dir = dir.Parent;
        return dir.FullName;
    }

    private sealed class ThrottleThenOk : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") });
        }
    }
}

public class ArmRoleAssignmentReaderTests
{
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample";
    private const string Principal = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task Returns_role_names_assigned_exactly_at_the_scope()
    {
        var h = new RouteHandler()
            .On(HttpMethod.Get, $"https://management.azure.com{Scope}/providers/Microsoft.Authorization/roleAssignments", $$$"""
                {"value":[
                  {"properties":{"scope":"{{{Scope}}}","roleDefinitionId":"/providers/Microsoft.Authorization/roleDefinitions/reader-id"}},
                  {"properties":{"scope":"/subscriptions/00000000-0000-0000-0000-000000000000","roleDefinitionId":"/providers/Microsoft.Authorization/roleDefinitions/owner-id"}}]}
                """)
            .On(HttpMethod.Get, "https://management.azure.com/providers/Microsoft.Authorization/roleDefinitions/reader-id", """{"properties":{"roleName":"Reader"}}""");
        var reader = new ArmRoleAssignmentReader(new HttpClient(h), new StaticCredential());
        var roles = await reader.ListForPrincipalAsync(Principal, Scope + "/", CancellationToken.None);
        Assert.Equal([new PmoReview.Core.Domain.AssignedRole("Reader", Scope)], roles);
        Assert.All(h.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
        Assert.Contains($"principalId eq '{Principal}'", h.Calls[0].Url);
    }

    [Theory]
    [InlineData("SAMPLE dbw-alpha-dev")]
    [InlineData(null)]
    public async Task Non_azure_scopes_are_not_readable(string? scope)
    {
        var reader = new ArmRoleAssignmentReader(new HttpClient(new RouteHandler()), new StaticCredential());
        Assert.Null(await reader.ListForPrincipalAsync(Principal, scope, CancellationToken.None));
    }
}

public class AzureDevOpsTrackerTests
{
    private static readonly AzureDevOpsOptions Options = new() { Organization = new Uri("https://dev.azure.com/sample"), Project = "Onboarding" };

    private static WorkItemDraft Draft(bool close = false) =>
        new("req-1", "SAMPLE-ALPHA", "Onboarding req-1", "Trail line 1\nTrail <b>2</b>", ["2026-10-17 [agent] review-pass: ok"], close, "pmo-request-req-1");

    [Fact]
    public async Task Creates_a_tagged_work_item_when_none_exists()
    {
        var h = new RouteHandler()
            .On(HttpMethod.Post, "https://dev.azure.com/sample/Onboarding/_apis/wit/wiql", """{"workItems":[]}""")
            .On(HttpMethod.Post, "https://dev.azure.com/sample/Onboarding/_apis/wit/workitems/$Task", """{"id":42,"_links":{"html":{"href":"https://dev.azure.com/sample/Onboarding/_workitems/edit/42"}}}""");
        var link = await new AzureDevOpsWorkItemTracker(new HttpClient(h), new StaticCredential(), Options).SyncAsync(Draft(), null, CancellationToken.None);
        Assert.Equal(42, link.WorkItemId);
        Assert.EndsWith("/42", link.Url);
        var create = h.Calls.Last();
        var ops = JsonDocument.Parse(create.Body).RootElement.EnumerateArray().ToDictionary(o => o.GetProperty("path").GetString()!, o => o.GetProperty("value").GetString()!);
        Assert.Equal("pmo-onboarding; pmo-request-req-1", ops["/fields/System.Tags"]);
        Assert.Contains("Trail &lt;b&gt;2&lt;/b&gt;", ops["/fields/System.Description"]);
        Assert.Contains("CONTAINS 'pmo-request-req-1'", JsonDocument.Parse(h.Calls[0].Body).RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Updates_the_existing_item_with_history_and_closes_it()
    {
        var h = new RouteHandler().On(HttpMethod.Patch, "https://dev.azure.com/sample/Onboarding/_apis/wit/workitems/42", """{"id":42}""");
        var link = await new AzureDevOpsWorkItemTracker(new HttpClient(h), new StaticCredential(), Options)
            .SyncAsync(Draft(close: true), new AdoLink { WorkItemId = 42 }, CancellationToken.None);
        Assert.Equal(42, link.WorkItemId);
        var ops = JsonDocument.Parse(h.Calls.Single().Body).RootElement.EnumerateArray().Select(o => o.GetProperty("path").GetString()).ToList();
        Assert.Contains("/fields/System.History", ops);
        Assert.Contains("/fields/System.State", ops);
    }

    [Fact]
    public async Task Rejected_call_becomes_a_category()
    {
        var h = new RouteHandler().On(HttpMethod.Post, "https://dev.azure.com/", "{}", HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<OutboundException>(() => new AzureDevOpsWorkItemTracker(new HttpClient(h), new StaticCredential(), Options).SyncAsync(Draft(), null, CancellationToken.None));
        Assert.Equal("AdoForbidden", e.Category);
    }
}

public class GraphMailSenderTests
{
    [Fact]
    public async Task Sends_text_mail_from_the_shared_mailbox()
    {
        var h = new RouteHandler().On(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users/sample.sender@sample.invalid/sendMail", "", HttpStatusCode.Accepted);
        var n = new Notification
        {
            Id = "n1", ProjectId = "SAMPLE-ALPHA", Kind = NotificationKind.Closure, IdempotencyKey = "k", To = ["a@sample.invalid"],
            Subject = "S", Body = "B", CreatedAt = DateTimeOffset.UnixEpoch, Status = NotificationStatus.Pending,
        };
        await new GraphMailSender(new HttpClient(h), new StaticCredential(), "sample.sender@sample.invalid").SendAsync(n, CancellationToken.None);
        var body = JsonDocument.Parse(h.Calls.Single().Body).RootElement;
        Assert.Equal("Text", body.GetProperty("message").GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("a@sample.invalid", body.GetProperty("message").GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.False(body.GetProperty("saveToSentItems").GetBoolean());
    }
}
