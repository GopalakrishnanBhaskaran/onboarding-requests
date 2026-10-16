using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Adapters.Entra;
using PmoReview.Core.Handoff;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>The one Entra write (auto-assign) against a mocked Graph v1.0. Checks run before the POST; nothing else is written.</summary>
public class GraphWriterTests
{
    private const string Pkg = "0b4c2a4e-6a51-4c47-9e0f-2f6f3c1d9a10";
    private const string Catalog = "1f0593d1-c71e-4d17-8068-7afa6bc24ea5";
    private const string Policy = "PMO Review auto-assign";
    private static readonly AssignmentOrder Order = new(Pkg, "sample.joiner@sample.onmicrosoft.com",
        new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 31, 23, 59, 59, TimeSpan.Zero), "PMO Review request req1 revision 1");

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri), body));
            return respond(request, body);
        }
    }

    private sealed class Token : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("sample-token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Policies(string catalog = Catalog, string name = Policy, string pkg = Pkg) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new { id = "pol-self", displayName = "Self request", catalog = new { id = catalog }, accessPackage = new { id = pkg } },
            new { id = "pol-app", displayName = name, catalog = new { id = catalog }, accessPackage = new { id = pkg } },
        },
    });

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> Graph(string? policies = null, HttpStatusCode user = HttpStatusCode.OK, HttpStatusCode post = HttpStatusCode.Created) =>
        (r, _) => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/assignmentPolicies") => Json(policies ?? Policies()),
            var p when p.Contains("/users/") => user == HttpStatusCode.OK ? Json("""{"id":"oid-joiner"}""") : Json("""{"error":{"message":"secret"}}""", user),
            var p when p.EndsWith("/assignmentRequests") => post == HttpStatusCode.Created
                ? Json("""{"id":"req-app-1","requestType":"adminAdd","state":"submitted","status":"Accepted","createdDateTime":"2026-10-16T09:00:00Z"}""", post)
                : Json("""{"error":{"message":"secret detail"}}""", post),
            _ => Json("{}", HttpStatusCode.NotFound),
        };

    private static GraphEntraAssignmentWriter Writer(Handler h) => new(new HttpClient(h), new Token(), Catalog, Policy);

    [Fact]
    public async Task Posts_one_admin_add_for_the_joiner_through_the_app_policy()
    {
        var h = new Handler(Graph());
        var created = await Writer(h).RequestAssignmentAsync(Order, CancellationToken.None);

        Assert.Equal(("req-app-1", "adminAdd", "submitted"), (created.Id, created.RequestType, created.State));
        Assert.Equal(3, h.Seen.Count);
        Assert.Equal(HttpMethod.Get, h.Seen[0].Method);
        Assert.Contains("/identityGovernance/entitlementManagement/assignmentPolicies?$expand=accessPackage,catalog&$filter=accessPackage/id eq '" + Pkg + "'", h.Seen[0].Url);
        Assert.Equal((HttpMethod.Get, "https://graph.microsoft.com/v1.0/users/sample.joiner@sample.onmicrosoft.com?$select=id"), (h.Seen[1].Method, h.Seen[1].Url));

        var (method, url, body) = h.Seen[2];
        Assert.Equal((HttpMethod.Post, "https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement/assignmentRequests"), (method, url));
        using var doc = JsonDocument.Parse(body!);
        var b = doc.RootElement;
        Assert.Equal("adminAdd", b.GetProperty("requestType").GetString());
        Assert.Equal("oid-joiner", b.GetProperty("assignment").GetProperty("targetId").GetString());
        Assert.Equal("pol-app", b.GetProperty("assignment").GetProperty("assignmentPolicyId").GetString());
        Assert.Equal(Pkg, b.GetProperty("assignment").GetProperty("accessPackageId").GetString());
        Assert.Equal("2026-11-02T00:00:00Z", b.GetProperty("schedule").GetProperty("startDateTime").GetString());
        Assert.Equal("afterDateTime", b.GetProperty("schedule").GetProperty("expiration").GetProperty("type").GetString());
        Assert.Equal("2027-03-31T23:59:59Z", b.GetProperty("schedule").GetProperty("expiration").GetProperty("endDateTime").GetString());
        // The only write ever sent:
        Assert.Single(h.Seen, s => s.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData("9f9f9f9f-0000-4000-8000-000000000001", Policy, "CatalogNotAllowed")]
    [InlineData(Catalog, "Some other policy", "PolicyNotFound")]
    public async Task Wrong_catalog_or_missing_policy_stops_before_any_write(string catalog, string policyName, string category)
    {
        var h = new Handler(Graph(policies: Policies(catalog, policyName)));
        var e = await Assert.ThrowsAsync<EntraWriteException>(() => Writer(h).RequestAssignmentAsync(Order, CancellationToken.None));
        Assert.Equal(category, e.Category);
        Assert.All(h.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    [Fact]
    public async Task Policy_name_match_ignores_case_and_extra_spaces()
    {
        var h = new Handler(Graph(policies: Policies(name: "  pmo review  AUTO-ASSIGN ")));
        Assert.Equal("req-app-1", (await Writer(h).RequestAssignmentAsync(Order, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Unknown_joiner_stops_before_any_write()
    {
        var h = new Handler(Graph(user: HttpStatusCode.NotFound));
        Assert.Equal("JoinerNotFound", (await Assert.ThrowsAsync<EntraWriteException>(() => Writer(h).RequestAssignmentAsync(Order, CancellationToken.None))).Category);
        Assert.All(h.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "AssignmentRejected")]
    [InlineData(HttpStatusCode.Forbidden, "GraphForbidden")]
    [InlineData(HttpStatusCode.InternalServerError, "GraphUnavailable")]
    public async Task Post_errors_are_categories_and_a_5xx_post_is_not_repeated(HttpStatusCode code, string category)
    {
        var h = new Handler(Graph(post: code));
        var e = await Assert.ThrowsAsync<EntraWriteException>(() => Writer(h).RequestAssignmentAsync(Order, CancellationToken.None));
        Assert.Equal(category, e.Category);
        Assert.DoesNotContain("secret", e.Message);
        Assert.Single(h.Seen, s => s.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Bad_ids_are_refused_before_any_call()
    {
        var h = new Handler(Graph());
        Assert.Equal("InvalidAccessPackageId", (await Assert.ThrowsAsync<EntraWriteException>(() =>
            Writer(h).RequestAssignmentAsync(Order with { AccessPackageId = "x' or 1 eq 1" }, CancellationToken.None))).Category);
        Assert.Empty(h.Seen);
        Assert.Throws<ArgumentException>(() => new GraphEntraAssignmentWriter(new HttpClient(h), new Token(), "not-a-guid", Policy));
    }
}
