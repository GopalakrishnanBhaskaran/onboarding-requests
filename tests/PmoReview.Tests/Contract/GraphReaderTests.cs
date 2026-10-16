using System.Net;
using System.Text;
using Azure.Core;
using PmoReview.Adapters.Entra;
using PmoReview.Core.Handoff;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>The Graph reader against a mocked handler (Graph v1.0 response shapes). No network, GET only.</summary>
public class GraphReaderTests
{
    private const string Pkg = "0b4c2a4e-6a51-4c47-9e0f-2f6f3c1d9a10";

    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(responses[Math.Min(Seen.Count - 1, responses.Length - 1)](request));
        }
    }

    private sealed class Token : TokenCredential
    {
        public List<string> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) { Scopes.AddRange(c.Scopes); return new("sample-token", DateTimeOffset.MaxValue); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Reads_requests_with_filter_paging_and_read_only_scope()
    {
        var handler = new Handler(
            _ => Json("""
                {"value":[{"id":"r1","requestType":"userAdd","state":"pendingApproval","status":"PendingApproval","createdDateTime":"2026-10-16T09:05:00Z",
                  "schedule":{"startDateTime":"2026-11-02T00:00:00Z","expiration":{"endDateTime":"2027-03-31T00:00:00Z","type":"afterDateTime"}},
                  "requestor":{"objectId":"oid-1","principalName":"sample.joiner@sample.onmicrosoft.com","email":"sample.joiner@sample.onmicrosoft.com","displayName":"SAMPLE Joiner"}}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement/assignmentRequests?$skiptoken=2"}
                """),
            _ => Json("""{"value":[{"id":"r2","requestType":"adminAdd","state":"delivered","createdDateTime":"2026-10-16T10:00:00Z"}]}"""));
        var token = new Token();
        var reader = new GraphEntraGovernanceReader(new HttpClient(handler), token);

        var items = await reader.ListRequestsAsync(Pkg, CancellationToken.None);

        Assert.Equal(["r1", "r2"], items.Select(i => i.Id));
        var r1 = items[0];
        Assert.Equal(("userAdd", "pendingApproval"), (r1.RequestType, r1.State));
        Assert.Equal("sample.joiner@sample.onmicrosoft.com", r1.Requestor!.PrincipalName);
        Assert.Equal(new DateTimeOffset(2027, 3, 31, 0, 0, 0, TimeSpan.Zero), r1.ScheduleEnd);
        Assert.All(handler.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.All(handler.Seen, r => Assert.Equal("Bearer sample-token", r.Headers.Authorization!.ToString()));
        Assert.Contains("accessPackage%2Fid%20eq%20%27" + Pkg + "%27", handler.Seen[0].RequestUri!.AbsoluteUri);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement/assignmentRequests?", handler.Seen[0].RequestUri!.AbsoluteUri);
        Assert.All(token.Scopes, s => Assert.Equal("https://graph.microsoft.com/.default", s));
    }

    [Fact]
    public async Task Reads_assignments_with_target_and_expiration_type()
    {
        var handler = new Handler(_ => Json("""
            {"value":[{"id":"a1","state":"delivered","status":"Delivered","expiredDateTime":null,
              "schedule":{"startDateTime":"2026-11-02T00:00:00Z","expiration":{"type":"noExpiration"}},
              "target":{"objectId":"oid-1","principalName":"sample.joiner@sample.onmicrosoft.com"}}]}
            """));
        var items = await new GraphEntraGovernanceReader(new HttpClient(handler), new Token()).ListAssignmentsAsync(Pkg, CancellationToken.None);

        var a = Assert.Single(items);
        Assert.Equal(("delivered", "noExpiration"), (a.State, a.ExpirationType));
        Assert.Null(a.ScheduleEnd);
        Assert.Contains("/entitlementManagement/assignments?", handler.Seen[0].RequestUri!.AbsoluteUri);
        Assert.Contains("$expand=accessPackage,target", Uri.UnescapeDataString(handler.Seen[0].RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task Next_links_outside_graph_are_not_followed()
    {
        var handler = new Handler(_ => Json("""{"value":[],"@odata.nextLink":"https://evil.example/steal"}"""));
        await new GraphEntraGovernanceReader(new HttpClient(handler), new Token()).ListRequestsAsync(Pkg, CancellationToken.None);
        Assert.Single(handler.Seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GraphForbidden", 1)]
    [InlineData(HttpStatusCode.NotFound, "AccessPackageNotFound", 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "GraphUnavailable", 2)]
    public async Task Errors_become_categories_without_details(HttpStatusCode code, string category, int calls)
    {
        var handler = new Handler(_ => Json("""{"error":{"code":"x","message":"secret detail"}}""", code));
        var e = await Assert.ThrowsAsync<EntraReadException>(() =>
            new GraphEntraGovernanceReader(new HttpClient(handler), new Token()).ListRequestsAsync(Pkg, CancellationToken.None));
        Assert.Equal(category, e.Category);
        Assert.DoesNotContain("secret", e.Message);
        Assert.Equal(calls, handler.Seen.Count);
    }

    [Fact]
    public async Task Non_guid_package_id_is_refused_before_any_call()
    {
        var handler = new Handler(_ => Json("""{"value":[]}"""));
        var e = await Assert.ThrowsAsync<EntraReadException>(() =>
            new GraphEntraGovernanceReader(new HttpClient(handler), new Token()).ListRequestsAsync("x' or 1 eq 1", CancellationToken.None));
        Assert.Equal("InvalidAccessPackageId", e.Category);
        Assert.Empty(handler.Seen);
    }
}
