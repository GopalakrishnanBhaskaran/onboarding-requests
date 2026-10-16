using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Adapters.Entra;
using PmoReview.Core.Handoff;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>Phase 2c Graph adapters against a mocked Graph v1.0: joiner accounts and package set-up. Writes are exactly the expected ones.</summary>
public class GraphProvisioningTests
{
    private const string Sku = "eec0eb4f-6444-4f95-aba0-50c24d67f998";
    private const string Catalog = "1f0593d1-c71e-4d17-8068-7afa6bc24ea5";

    private sealed class Graph(Func<HttpMethod, string, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            var url = Uri.UnescapeDataString(r.RequestUri!.AbsoluteUri);
            Seen.Add((r.Method, url, body));
            return respond(r.Method, url, body);
        }
        public IEnumerable<(HttpMethod Method, string Url, string? Body)> Writes => Seen.Where(s => s.Method != HttpMethod.Get);
    }

    private sealed class Token : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("sample-token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => Json("""{"error":{"message":"secret detail"}}""", HttpStatusCode.NotFound);

    [Fact]
    public async Task Joiner_account_licence_and_pass_use_exactly_three_writes()
    {
        var g = new Graph((m, url, _) => (m.Method, url) switch
        {
            ("GET", var u) when u.Contains("/users/sample.joiner2@") => NotFound(),
            ("POST", var u) when u.EndsWith("/v1.0/users") => Json(new { id = "oid-new" }, HttpStatusCode.Created),
            ("POST", var u) when u.EndsWith("/assignLicense") => Json(new { id = "oid-new" }),
            ("POST", var u) when u.EndsWith("/temporaryAccessPassMethods") =>
                Json(new { id = "tap-1", temporaryAccessPass = "TAP-SECRET", startDateTime = "2026-11-02T00:00:00Z", lifetimeInMinutes = 480 }, HttpStatusCode.Created),
            _ => NotFound(),
        });
        var dir = new GraphJoinerDirectory(new HttpClient(g), new Token(), Sku);

        Assert.Null(await dir.FindUserAsync("sample.joiner2@sample.onmicrosoft.com", CancellationToken.None));
        var id = await dir.CreateUserAsync(new NewJoinerAccount("sample.joiner2@sample.onmicrosoft.com", "Sample Joiner2", "sample.joiner2", "IN"), CancellationToken.None);
        await dir.AssignLicenseAsync(id, CancellationToken.None);
        var tap = await dir.CreateTemporaryAccessPassAsync(id, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), 480, CancellationToken.None);

        Assert.Equal("oid-new", id);
        Assert.Equal(("TAP-SECRET", new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)), (tap.Secret, tap.ExpiresAt));
        var writes = g.Writes.ToList();
        Assert.Equal(3, writes.Count);

        using var create = JsonDocument.Parse(writes[0].Body!);
        Assert.Equal("sample.joiner2@sample.onmicrosoft.com", create.RootElement.GetProperty("userPrincipalName").GetString());
        Assert.True(create.RootElement.GetProperty("accountEnabled").GetBoolean());
        var pw = create.RootElement.GetProperty("passwordProfile");
        Assert.True(pw.GetProperty("forceChangePasswordNextSignIn").GetBoolean());
        Assert.Equal(32, pw.GetProperty("password").GetString()!.Length);

        using var lic = JsonDocument.Parse(writes[1].Body!);
        Assert.Equal(Sku, lic.RootElement.GetProperty("addLicenses")[0].GetProperty("skuId").GetString());
        Assert.Equal(0, lic.RootElement.GetProperty("removeLicenses").GetArrayLength());

        using var t = JsonDocument.Parse(writes[2].Body!);
        Assert.Equal("2026-11-02T00:00:00Z", t.RootElement.GetProperty("startDateTime").GetString());
        Assert.False(t.RootElement.GetProperty("isUsableOnce").GetBoolean());
        Assert.All(writes, w => Assert.Equal(HttpMethod.Post, w.Method)); // no PATCH, no DELETE
    }

    [Fact]
    public void Random_password_has_all_character_classes()
    {
        for (var i = 0; i < 50; i++)
        {
            var p = GraphJoinerDirectory.RandomPassword();
            Assert.Equal(32, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public async Task No_licence_seats_is_a_category_without_details()
    {
        var g = new Graph((_, _, _) => Json("""{"error":{"message":"secret: Subscription does not have any available licenses."}}""", HttpStatusCode.BadRequest));
        var e = await Assert.ThrowsAsync<EntraWriteException>(() => new GraphJoinerDirectory(new HttpClient(g), new Token(), Sku).AssignLicenseAsync("oid", CancellationToken.None));
        Assert.Equal("LicenseUnavailable", e.Category);
        Assert.DoesNotContain("secret", e.Message);
    }

    [Fact]
    public async Task Package_setup_creates_group_resource_package_scope_and_policy_in_the_allowed_catalog()
    {
        var resourceAdded = false;
        var g = new Graph((m, url, _) =>
        {
            if (m == HttpMethod.Get && url.Contains("/v1.0/groups?")) return Json(new { value = Array.Empty<object>() });
            if (m == HttpMethod.Post && url.EndsWith("/v1.0/groups")) return Json(new { id = "grp-1" }, HttpStatusCode.Created);
            if (m == HttpMethod.Get && url.Contains($"/catalogs/{Catalog}/resources?"))
                return Json(new { value = resourceAdded ? new object[] { new { id = "res-1", originId = "grp-1" } } : [] });
            if (m == HttpMethod.Post && url.EndsWith("/resourceRequests")) { resourceAdded = true; return Json(new { id = "rr-1" }, HttpStatusCode.Created); }
            if (m == HttpMethod.Get && url.Contains($"/catalogs/{Catalog}/resourceRoles?"))
                return Json(new { value = new[] { new { id = "role-o", displayName = "Owner", originId = "Owner_grp-1" }, new { id = "role-m", displayName = "Member", originId = "Member_grp-1" } } });
            if (m == HttpMethod.Get && url.EndsWith($"/catalogs/{Catalog}/accessPackages")) return Json(new { value = Array.Empty<object>() });
            if (m == HttpMethod.Post && url.EndsWith("/accessPackages")) return Json(new { id = "pkg-1" }, HttpStatusCode.Created);
            if (m == HttpMethod.Get && url.Contains("/accessPackages/pkg-1?")) return Json(new { id = "pkg-1", resourceRoleScopes = Array.Empty<object>() });
            if (m == HttpMethod.Post && url.EndsWith("/accessPackages/pkg-1/resourceRoleScopes")) return Json(new { id = "scope-1" }, HttpStatusCode.Created);
            if (m == HttpMethod.Get && url.Contains("/assignmentPolicies?")) return Json(new { value = new[] { new { id = "p-self", displayName = "Self request" } } });
            if (m == HttpMethod.Post && url.EndsWith("/assignmentPolicies")) return Json(new { id = "p-app" }, HttpStatusCode.Created);
            return NotFound();
        });
        var id = await new GraphPackageProvisioner(new HttpClient(g), new Token(), Catalog, "PMO Review auto-assign")
            .EnsureAsync(new PackageSpec("SAMPLE-GRP-ALPHA-DEV-NEW", "SAMPLE Data Engineer – Dev [A-MAP-NEW]", "d"), CancellationToken.None);

        Assert.Equal("pkg-1", id);
        var writes = g.Writes.Select(w => w.Url[(w.Url.IndexOf("/v1.0/", StringComparison.Ordinal) + 6)..]).ToList();
        Assert.Equal(["groups", "identityGovernance/entitlementManagement/resourceRequests", "identityGovernance/entitlementManagement/accessPackages",
                      "identityGovernance/entitlementManagement/accessPackages/pkg-1/resourceRoleScopes", "identityGovernance/entitlementManagement/assignmentPolicies"], writes);

        var bodies = g.Writes.Select(w => JsonDocument.Parse(w.Body!).RootElement).ToList();
        Assert.True(bodies[0].GetProperty("securityEnabled").GetBoolean());
        Assert.False(bodies[0].GetProperty("mailEnabled").GetBoolean());
        Assert.False(bodies[0].TryGetProperty("isAssignableToRole", out _)); // never a role-assignable group
        Assert.Equal(Catalog, bodies[1].GetProperty("catalog").GetProperty("id").GetString());
        Assert.Equal(Catalog, bodies[2].GetProperty("catalog").GetProperty("id").GetString());
        Assert.Equal("Member_grp-1", bodies[3].GetProperty("role").GetProperty("originId").GetString());
        Assert.Equal("notSpecified", bodies[4].GetProperty("allowedTargetScope").GetString());
        Assert.False(bodies[4].GetProperty("requestorSettings").GetProperty("enableTargetsToSelfAddAccess").GetBoolean());
        Assert.False(bodies[4].GetProperty("requestApprovalSettings").GetProperty("isApprovalRequiredForAdd").GetBoolean());
    }

    [Theory]
    [InlineData(true, true)]   // role-assignable
    [InlineData(false, false)] // not a security group
    public async Task Existing_privileged_or_non_security_group_is_refused_before_any_write(bool roleAssignable, bool security)
    {
        var g = new Graph((m, url, _) => m == HttpMethod.Get && url.Contains("/v1.0/groups?")
            ? Json(new { value = new[] { new { id = "grp-x", displayName = "SAMPLE-GRP-X", securityEnabled = security, isAssignableToRole = roleAssignable } } })
            : NotFound());
        var e = await Assert.ThrowsAsync<EntraWriteException>(() => new GraphPackageProvisioner(new HttpClient(g), new Token(), Catalog, "PMO Review auto-assign")
            .EnsureAsync(new PackageSpec("SAMPLE-GRP-X", "p", "d"), CancellationToken.None));
        Assert.Equal("GroupNotAllowed", e.Category);
        Assert.Empty(g.Writes);
    }

    [Fact]
    public async Task Existing_package_scope_and_policy_are_reused_without_writes()
    {
        var g = new Graph((m, url, _) =>
        {
            if (url.Contains("/v1.0/groups?")) return Json(new { value = new[] { new { id = "grp-1", securityEnabled = true, isAssignableToRole = false } } });
            if (url.Contains("/resources?")) return Json(new { value = new[] { new { id = "res-1" } } });
            if (url.Contains("/resourceRoles?")) return Json(new { value = new[] { new { id = "role-m", displayName = "Member", originId = "Member_grp-1" } } });
            if (url.EndsWith("/accessPackages")) return Json(new { value = new[] { new { id = "pkg-1", displayName = "P" } } });
            if (url.Contains("/accessPackages/pkg-1?")) return Json(new { id = "pkg-1", resourceRoleScopes = new[] { new { scope = new { originId = "grp-1" } } } });
            if (url.Contains("/assignmentPolicies?")) return Json(new { value = new[] { new { id = "p-app", displayName = "pmo review AUTO-ASSIGN" } } });
            return NotFound();
        });
        Assert.Equal("pkg-1", await new GraphPackageProvisioner(new HttpClient(g), new Token(), Catalog, "PMO Review auto-assign")
            .EnsureAsync(new PackageSpec("SAMPLE-GRP-1", "P", "d"), CancellationToken.None));
        Assert.Empty(g.Writes);
    }
}
