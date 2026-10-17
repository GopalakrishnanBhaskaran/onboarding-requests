using System.Text;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

public class IdentityTests
{
    private static readonly string[] Aad = ["aad"];

    [Fact]
    public async Task Missing_principal_header_is_401()
    {
        Assert.Equal(401, (await TestApp.Create().Call(user: null, "GET", "me")).Status);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("e30=")]     // {}
    [InlineData("bnVsbA==")] // null
    public async Task Malformed_principal_is_401(string header)
    {
        var r = await TestApp.Create().Send("GET", "me", new() { [ClientPrincipalParser.HeaderName] = header }, null);
        Assert.Equal(401, r.Status);
    }

    [Fact]
    public void Non_entra_provider_and_unauthenticated_principals_are_rejected()
    {
        Assert.Equal(ErrorKind.Unauthenticated, Assert.Throws<AppException>(() => ClientPrincipalParser.Parse(TestApp.Principal(TestApp.Alice, provider: "github"), Aad)).Kind);
        Assert.Throws<AppException>(() => ClientPrincipalParser.Parse(TestApp.Principal(TestApp.Alice, "aad", "anonymous"), Aad));
        Assert.Throws<AppException>(() => ClientPrincipalParser.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 9000))), Aad));
    }

    [Fact]
    public async Task Signed_in_user_without_access_rows_sees_nothing()
    {
        var app = TestApp.Create();
        var me = await app.Call(TestApp.Stranger, "GET", "me");
        Assert.Equal(200, me.Status);
        Assert.Equal(0, me.Json.GetProperty("projects").GetArrayLength());
        Assert.Equal(404, (await app.Call(TestApp.Stranger, "GET", $"projects/{TestApp.Alpha}/requests")).Status);
        Assert.Empty((await app.Call(TestApp.Stranger, "GET", "queue")).Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Me_lists_v2_roles_per_project()
    {
        var me = await TestApp.Create().Call(TestApp.Bella, "GET", "me");
        var p = Assert.Single(me.Json.GetProperty("projects").EnumerateArray());
        Assert.Equal(TestApp.Beta, p.GetProperty("projectId").GetString());
        Assert.Equal(["Owner", "Security", "PMO"], p.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }
}

public class PermissionTests
{
    [Theory]
    [InlineData(AppRole.Requester, Permission.SubmitRequests, true)]
    [InlineData(AppRole.Requester, Permission.ApproveAsOwner, false)]
    [InlineData(AppRole.Owner, Permission.ApproveAsOwner, true)]
    [InlineData(AppRole.Owner, Permission.ApproveAsSecurity, false)]
    [InlineData(AppRole.Security, Permission.ApproveAsSecurity, true)]
    [InlineData(AppRole.Security, Permission.SubmitRequests, false)]
    [InlineData(AppRole.IdTeam, Permission.WorkIdTeamTasks, true)]
    [InlineData(AppRole.IdTeam, Permission.UpdateMappingGroup, true)]
    [InlineData(AppRole.IdTeam, Permission.EditMappingSheet, false)]
    [InlineData(AppRole.IdTeam, Permission.ApproveAsSecurity, false)]
    [InlineData(AppRole.RoleTeam, Permission.WorkRoleTeamTasks, true)]
    [InlineData(AppRole.RoleTeam, Permission.WorkIdTeamTasks, false)]
    [InlineData(AppRole.PMO, Permission.EditMappingSheet, true)]
    [InlineData(AppRole.PMO, Permission.ApproveAsOwner, false)]
    [InlineData(AppRole.PMO, Permission.ApproveAsSecurity, false)]
    [InlineData(AppRole.Operator, Permission.RecoverRuns, true)]
    [InlineData(AppRole.Operator, Permission.ApproveAsOwner, false)]
    public void Role_table(AppRole role, Permission permission, bool allowed) =>
        Assert.Equal(allowed, AccessPolicy.Allows([role], permission));

    [Fact]
    public void Only_owner_and_security_roles_can_approve()
    {
        var approvers = Enum.GetValues<AppRole>().Where(r => AccessPolicy.Allows([r], Permission.ApproveAsOwner) || AccessPolicy.Allows([r], Permission.ApproveAsSecurity));
        Assert.Equal([AppRole.Owner, AppRole.Security], approvers.ToArray());
    }

    [Theory]
    [InlineData(AppRole.Reviewer)]
    [InlineData(AppRole.Maintainer)]
    public void Legacy_v1_roles_grant_nothing(AppRole role) =>
        Assert.All(Enum.GetValues<Permission>(), p => Assert.False(AccessPolicy.Allows([role], p)));

    [Fact]
    public void No_route_creates_users_groups_or_role_assignments_or_handles_a_pass()
    {
        var routes = TestApp.Create().Host.Routes;
        foreach (var forbidden in new[] { "grant", "assign", "provision", "users", "groups", "pass", "secret", "delete", "revoke" })
            Assert.DoesNotContain(routes, r => r.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, r => r.StartsWith("DELETE", StringComparison.Ordinal) || r.StartsWith("PATCH", StringComparison.Ordinal));
    }
}

public class CrossProjectTests
{
    [Fact]
    public async Task Other_project_users_get_404_for_every_request_route()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        foreach (var (method, path) in new[]
                 {
                     ("GET", $"projects/{TestApp.Alpha}/requests/{id}"), ("GET", $"projects/{TestApp.Alpha}/requests/{id}/runs"),
                     ("GET", $"projects/{TestApp.Alpha}/requests/{id}/trail"), ("POST", $"projects/{TestApp.Alpha}/requests/{id}/tasks/id-team/pickup"),
                     ("GET", $"projects/{TestApp.Alpha}/mapping-sheet"), ("GET", $"projects/{TestApp.Alpha}/issues"),
                 })
            Assert.Equal(404, (await app.Call(TestApp.Bob, method, path, method == "POST" ? new { } : null)).Status);
    }

    [Fact]
    public async Task Project_in_the_route_scopes_every_read()
    {
        var app = TestApp.Create();
        var alpha = await app.SubmitId();
        var beta = await app.SubmitId(TestApp.Intake(resource: "SAMPLE Test Portal", owner: TestApp.SignIn(TestApp.Bella)), TestApp.Bob, TestApp.Beta);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Beta}/requests/{alpha}")).Status);
        var list = await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Beta}/requests");
        Assert.Equal([beta], list.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("requestId").GetString()!).ToArray());
    }

    [Fact]
    public async Task Beta_mapping_rows_are_not_visible_to_alpha_reviews()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(resource: "SAMPLE Test Portal"));
        var r = await app.Request(id);
        Assert.Equal(RequestStatus.Returned, r.Flow!.Status);
        Assert.Contains("no row for resource 'SAMPLE Test Portal'", r.Flow.LastReason);
    }

    [Fact]
    public async Task Issues_are_for_security_and_operators_only()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        await app.Approve(id, TestApp.Otto);
        Assert.Equal(200, (await app.Call(TestApp.Sam, "GET", $"projects/{TestApp.Alpha}/issues")).Status);
        Assert.Equal(200, (await app.Call(TestApp.Oscar, "GET", $"projects/{TestApp.Alpha}/issues")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/issues")).Status);
        Assert.Empty((await app.View(id, TestApp.Alice)).Json.GetProperty("issues").EnumerateArray());
        Assert.NotEmpty((await app.View(id, TestApp.Sam)).Json.GetProperty("issues").EnumerateArray());
    }
}

public class RunAccessTests
{
    [Fact]
    public async Task Runs_are_visible_in_the_project_and_retry_is_operator_only()
    {
        var app = TestApp.With(new FakeModel((_, _) => throw new HttpRequestException("down")));
        var id = await app.SubmitId();
        var runs = await app.Call(TestApp.Olivia, "GET", $"projects/{TestApp.Alpha}/requests/{id}/runs");
        Assert.Equal(200, runs.Status);
        var runId = runs.Json.GetProperty("items")[0].GetProperty("value").GetProperty("id").GetString()!;
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Alpha}/runs/{runId}")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/runs/{runId}/retry", new { nonce = "nonce-0001" })).Status);
        Assert.Equal(400, (await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/runs/{runId}/retry", new { nonce = "x" })).Status);
        Assert.Equal(202, (await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/runs/{runId}/retry", new { nonce = "nonce-0001" })).Status);
    }

    [Fact]
    public async Task Operator_can_advance_a_request_without_the_worker()
    {
        var app = TestApp.With(apiDirectory: FakeDirectory.SeededFromSample());
        var id = await app.SubmitId();
        Assert.Equal(200, (await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/requests/{id}/advance")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests/{id}/advance")).Status);
    }
}
