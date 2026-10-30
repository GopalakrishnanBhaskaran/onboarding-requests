using System.Net;
using PmoReview.Adapters.Entra;
using PmoReview.Adapters.Rbac;
using PmoReview.Core.Directory;
using PmoReview.Tests.Unit;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>
/// v1.1 F5 adapters against mocked HTTP (no real Graph or ARM call, R9): exactly the documented calls (F71–F75, F77, F79,
/// F82–F84, F94, F102), membership removal always through <c>/$ref</c>, and an Azure role assignment deleted only when it
/// belongs to the user being offboarded and that principal is a user.
/// </summary>
public class OffboardingAdapterTests
{
    private const string G = "https://graph.microsoft.com/v1.0/";
    private const string Arm = "https://management.azure.com";
    private const string User = "11111111-1111-4111-8111-111111111111";
    private const string Group = "22222222-2222-4222-8222-222222222222";
    private const string Dynamic = "33333333-3333-4333-8333-333333333333";
    private const string Role = "44444444-4444-4444-8444-444444444444";
    private const string Tap = "55555555-5555-4555-8555-555555555555";
    private const string Sub = "66666666-6666-4666-8666-666666666666";
    private static readonly string AssignmentId = $"/subscriptions/{Sub}/resourceGroups/rg-sample/providers/Microsoft.Authorization/roleAssignments/77777777-7777-4777-8777-777777777777";

    [Fact]
    public async Task The_graph_writer_calls_exactly_the_five_documented_urls()
    {
        var h = new RouteHandler()
            .On(HttpMethod.Post, $"{G}users/{User}/revokeSignInSessions", "{\"value\":true}")
            .On(HttpMethod.Delete, $"{G}users/{User}/authentication/temporaryAccessPassMethods/{Tap}", "", HttpStatusCode.NoContent)
            .On(HttpMethod.Delete, $"{G}groups/{Group}/members/{User}/$ref", "", HttpStatusCode.NoContent)
            .On(HttpMethod.Patch, $"{G}users/{User}", "", HttpStatusCode.NoContent)
            .On(HttpMethod.Delete, $"{G}users/{User}", "", HttpStatusCode.NoContent);
        var w = new GraphOffboardingWriter(new HttpClient(h), new StaticCredential(), deleteUser: true);
        await w.RevokeSessionsAsync(User, CancellationToken.None);
        await w.DeleteTapAsync(User, Tap, CancellationToken.None);
        await w.RemoveMemberAsync(Group, User, CancellationToken.None);
        await w.DisableAsync(User, CancellationToken.None);
        await w.DeleteUserAsync(User, CancellationToken.None);
        Assert.Equal([
            ("POST", $"{G}users/{User}/revokeSignInSessions"),
            ("DELETE", $"{G}users/{User}/authentication/temporaryAccessPassMethods/{Tap}"),
            ("DELETE", $"{G}groups/{Group}/members/{User}/$ref"),
            ("PATCH", $"{G}users/{User}"),
            ("DELETE", $"{G}users/{User}"),
        ], h.Calls.Select(c => (c.Method.Method, c.Url)));
        Assert.Equal("{\"accountEnabled\":false}", h.Calls[3].Body);
    }

    [Fact]
    public async Task The_graph_writer_refuses_anything_else_and_treats_gone_as_done()
    {
        var h = new RouteHandler(); // every call answers 404
        var w = new GraphOffboardingWriter(new HttpClient(h), new StaticCredential());
        await w.RemoveMemberAsync(Group, User, CancellationToken.None);   // already removed: done
        await w.DeleteTapAsync(User, Tap, CancellationToken.None);
        Assert.Equal("WriteRefused", (await Assert.ThrowsAsync<DirectoryWriteException>(() => w.DeleteUserAsync(User, CancellationToken.None))).Category); // deletion off
        Assert.Equal("WriteRefused", (await Assert.ThrowsAsync<DirectoryWriteException>(() => w.RemoveMemberAsync("not-a-guid", User, CancellationToken.None))).Category);
        Assert.False(GraphOffboardingWriter.Allowed(HttpMethod.Delete, $"{G}groups/{Group}/members/{User}", null));          // without /$ref: never (F73)
        Assert.False(GraphOffboardingWriter.Allowed(HttpMethod.Patch, $"{G}users/{User}", "{\"accountEnabled\":true}"));    // only disabling
        Assert.False(GraphOffboardingWriter.Allowed(HttpMethod.Patch, $"{G}users/{User}", "{\"passwordProfile\":{}}"));
        Assert.False(GraphOffboardingWriter.Allowed(HttpMethod.Delete, $"{G}groups/{Group}", null));
        Assert.False(GraphOffboardingWriter.Allowed(HttpMethod.Post, $"{G}users/{User}/revokeSignInSessions/../../users", null));
        Assert.Equal(2, h.Calls.Count);

        var refused = new RouteHandler().On(HttpMethod.Post, $"{G}users/{User}/revokeSignInSessions", "{}", HttpStatusCode.Forbidden);
        var e = await Assert.ThrowsAsync<DirectoryWriteException>(() => new GraphOffboardingWriter(new HttpClient(refused), new StaticCredential()).RevokeSessionsAsync(User, CancellationToken.None));
        Assert.True(e.Forbidden);
    }

    [Fact]
    public async Task The_reader_plans_memberships_with_getMemberGroups_and_direct_member_reads()
    {
        var h = new RouteHandler()
            .On(HttpMethod.Post, $"{G}users/{User}/getMemberGroups", $"{{\"value\":[\"{Group}\",\"{Dynamic}\",\"{Role}\"]}}")
            .On(HttpMethod.Get, $"{G}groups/{Group}?", $"{{\"id\":\"{Group}\",\"displayName\":\"SAMPLE-GRP\",\"groupTypes\":[]}}")
            .On(HttpMethod.Get, $"{G}groups/{Group}/members?", $"{{\"value\":[{{\"id\":\"{User}\"}}]}}")
            .On(HttpMethod.Get, $"{G}groups/{Dynamic}?", $"{{\"id\":\"{Dynamic}\",\"displayName\":\"SAMPLE-DYN\",\"groupTypes\":[\"DynamicMembership\"]}}")
            .On(HttpMethod.Get, $"{G}users/{User}/authentication/methods",
                $"{{\"value\":[{{\"@odata.type\":\"#microsoft.graph.passwordAuthenticationMethod\",\"id\":\"x\"}},{{\"@odata.type\":\"#microsoft.graph.temporaryAccessPassAuthenticationMethod\",\"id\":\"{Tap}\"}}]}}");
        var r = new GraphDirectoryReader(new HttpClient(h), new StaticCredential());
        var m = await r.MembershipsAsync(User, CancellationToken.None);
        Assert.Equal([(Group, true, false), (Dynamic, true, true), (Role, true, true)], m.Select(x => (x.GroupObjectId, x.Direct, x.NotChangeable is not null)));
        Assert.Contains("directory role", m.Single(x => x.GroupObjectId == Role).NotChangeable);
        Assert.Equal([Tap], await r.TapMethodIdsAsync(User, CancellationToken.None));
        Assert.Contains("\"securityEnabledOnly\":false", h.Calls[0].Body);
        Assert.Contains(h.Calls, c => c.Url.Contains($"/members?$filter=id eq '{User}'&$count=true", StringComparison.Ordinal));
        Assert.All(h.Calls.Skip(1), c => Assert.Equal(HttpMethod.Get, c.Method));
    }

    [Fact]
    public async Task The_arm_remover_deletes_only_the_offboarded_users_own_assignment()
    {
        string Assignment(string principal, string type) =>
            $"{{\"id\":\"{AssignmentId}\",\"properties\":{{\"principalId\":\"{principal}\",\"principalType\":\"{type}\",\"scope\":\"/subscriptions/{Sub}\"}}}}";
        var a = new RoleAssignmentRef(AssignmentId, $"/subscriptions/{Sub}", "acdd72a7-3385-48ef-bd42-f606fba81ae7", User, "User");

        var ok = new RouteHandler().On(HttpMethod.Get, $"{Arm}{AssignmentId}", Assignment(User, "User")).On(HttpMethod.Delete, $"{Arm}{AssignmentId}", "{}");
        await new ArmRoleAssignmentRemover(new HttpClient(ok), new StaticCredential()).DeleteAsync(a, User, CancellationToken.None);
        Assert.Equal(["GET", "DELETE"], ok.Calls.Select(c => c.Method.Method));
        Assert.Contains("api-version=2022-04-01", ok.Calls[1].Url);

        foreach (var (principal, type) in new[] { (Group, "User"), (User, "Group"), (User, "ServicePrincipal") })
        {
            var other = new RouteHandler().On(HttpMethod.Get, $"{Arm}{AssignmentId}", Assignment(principal, type)).On(HttpMethod.Delete, $"{Arm}{AssignmentId}", "{}");
            var e = await Assert.ThrowsAsync<DirectoryWriteException>(() => new ArmRoleAssignmentRemover(new HttpClient(other), new StaticCredential()).DeleteAsync(a, User, CancellationToken.None));
            Assert.Equal("WriteRefused", e.Category);
            Assert.DoesNotContain(other.Calls, c => c.Method == HttpMethod.Delete);
        }

        var gone = new RouteHandler(); // 404 on the read: already removed
        await new ArmRoleAssignmentRemover(new HttpClient(gone), new StaticCredential()).DeleteAsync(a, User, CancellationToken.None);
        Assert.Single(gone.Calls);
        Assert.False(ArmRoleAssignmentRemover.IsAssignmentId($"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions/{Group}"));
    }

    [Fact]
    public async Task The_arm_reader_lists_subscriptions_and_a_users_assignments_and_reports_unreadable_ones()
    {
        var h = new RouteHandler()
            .On(HttpMethod.Get, $"{Arm}/subscriptions?api-version=2022-12-01", $"{{\"value\":[{{\"subscriptionId\":\"{Sub}\"}}]}}")
            .On(HttpMethod.Get, $"{Arm}/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignments",
                $"{{\"value\":[{{\"id\":\"{AssignmentId}\",\"properties\":{{\"principalId\":\"{User}\",\"principalType\":\"User\",\"scope\":\"/subscriptions/{Sub}/resourceGroups/rg-sample\",\"roleDefinitionId\":\"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7\"}}}}]}}");
        var r = new ArmRoleAssignmentReader(new HttpClient(h), new StaticCredential());
        Assert.Equal([Sub], await r.SubscriptionsAsync(CancellationToken.None));
        var list = await r.AssignmentsInSubscriptionAsync(Sub, User, CancellationToken.None);
        Assert.Equal((AssignmentId, "acdd72a7-3385-48ef-bd42-f606fba81ae7", User, "User"), Assert.Single(list!).Pipe(x => (x.Id, x.RoleDefinitionId, x.PrincipalId, x.PrincipalType)));
        Assert.Contains($"$filter=principalId eq '{User}'", h.Calls[1].Url);

        var forbidden = new RouteHandler().On(HttpMethod.Get, $"{Arm}/subscriptions/{Sub}/providers", "{}", HttpStatusCode.Forbidden);
        Assert.Null(await new ArmRoleAssignmentReader(new HttpClient(forbidden), new StaticCredential()).AssignmentsInSubscriptionAsync(Sub, User, CancellationToken.None));
        Assert.All(h.Calls.Concat(forbidden.Calls), c => Assert.Equal(HttpMethod.Get, c.Method));
    }
}
