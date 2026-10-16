using System.Text;
using PmoReview.Core.Common;
using PmoReview.Core.Security;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

public class IdentityTests
{
    private static readonly string[] Aad = ["aad"];

    [Fact]
    public async Task Missing_principal_header_is_401()
    {
        var app = TestApp.Create();
        var r = await app.Call(user: null, "GET", "me");
        Assert.Equal(401, r.Status);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("e30=")]                       // {}
    [InlineData("bnVsbA==")]                   // null
    public async Task Malformed_principal_is_401(string header)
    {
        var app = TestApp.Create();
        var r = await app.Send("GET", "me", new() { [ClientPrincipalParser.HeaderName] = header }, null);
        Assert.Equal(401, r.Status);
    }

    [Fact]
    public void Non_entra_provider_is_rejected()
    {
        var header = TestApp.Principal(TestApp.Alice, provider: "github");
        var e = Assert.Throws<AppException>(() => ClientPrincipalParser.Parse(header, Aad));
        Assert.Equal(ErrorKind.Unauthenticated, e.Kind);
    }

    [Fact]
    public void Principal_without_authenticated_role_is_rejected()
    {
        var header = TestApp.Principal(TestApp.Alice, "aad", "anonymous");
        Assert.Throws<AppException>(() => ClientPrincipalParser.Parse(header, Aad));
    }

    [Fact]
    public void Oversized_header_is_rejected()
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 9000)));
        Assert.Throws<AppException>(() => ClientPrincipalParser.Parse(header, Aad));
    }

    [Fact]
    public async Task Signed_in_user_without_access_rows_sees_nothing()
    {
        var app = TestApp.Create();
        var me = await app.Call(TestApp.Stranger, "GET", "me");
        Assert.Equal(200, me.Status);
        Assert.Equal(0, me.Json.GetProperty("projects").GetArrayLength());

        var list = await app.Call(TestApp.Stranger, "GET", $"projects/{TestApp.Alpha}/requests");
        Assert.Equal(404, list.Status);
    }

    [Fact]
    public async Task Identity_in_body_is_ignored()
    {
        var app = TestApp.Create();
        // Bob (Beta PMO) claims to be Alice in the body and posts to his own project.
        var r = await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Beta}/requests",
            new { userId = TestApp.Alice, createdBy = TestApp.Alice, role = "SAMPLE Tester" });
        Assert.Equal(201, r.Status);
        Assert.Equal(TestApp.Bob, r.Value.GetProperty("createdBy").GetString());
    }
}
