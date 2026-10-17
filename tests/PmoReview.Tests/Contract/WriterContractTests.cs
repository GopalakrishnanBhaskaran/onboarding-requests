using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Adapters.Entra;
using PmoReview.Adapters.Rbac;
using PmoReview.Core.Directory;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>v2.1 writers against mocked Graph / ARM: exact create calls (VERIFIED_FACTS F56–F59), idempotent repeats, categories.</summary>
public class GraphDirectoryWriterTests
{
    private const string G = "https://graph.microsoft.com/v1.0/";
    private const string AgentOid = "33333333-3333-3333-3333-333333333333";
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string GroupId = "22222222-2222-2222-2222-222222222222";

    /// <summary>A token whose payload carries the agent's own object id (the group owner).</summary>
    private sealed class OidCredential : TokenCredential
    {
        private static readonly string Token = $"e30.{Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"oid":"{{AgentOid}}"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.sig";
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new(Token, DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
    }

    private static (GraphDirectoryWriter Writer, RouteHandler Http) Writer(Action<RouteHandler> routes)
    {
        var h = new RouteHandler();
        routes(h);
        return (new GraphDirectoryWriter(new HttpClient(h), new OidCredential()), h);
    }

    [Fact]
    public async Task Creates_a_member_user_with_a_random_password_that_is_never_returned()
    {
        var (writer, http) = Writer(h => h.On(HttpMethod.Post, $"{G}users", $$"""{"id":"{{UserId}}"}""", HttpStatusCode.Created));
        var user = await writer.CreateUserAsync(new NewUser("sample.e2001@sample.invalid", "Sample Joiner", "sample.e2001", "Sample", "Joiner"), CancellationToken.None);

        Assert.Equal((UserId, false), (user.ObjectId, user.IsGuest));
        var call = Assert.Single(http.Calls);
        Assert.Equal((HttpMethod.Post, $"{G}users"), (call.Method, call.Url));
        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.True(body.GetProperty("accountEnabled").GetBoolean());
        Assert.Equal("sample.e2001@sample.invalid", body.GetProperty("userPrincipalName").GetString());
        Assert.Equal("sample.e2001", body.GetProperty("mailNickname").GetString());
        var password = body.GetProperty("passwordProfile").GetProperty("password").GetString()!;
        Assert.True(password.Length >= 32 && body.GetProperty("passwordProfile").GetProperty("forceChangePasswordNextSignIn").GetBoolean());
        Assert.DoesNotContain(password, user.ToString());
    }

    [Fact]
    public async Task Creates_a_security_group_owned_by_the_agent_then_adds_the_member()
    {
        var (writer, http) = Writer(h => h
            .On(HttpMethod.Post, $"{G}groups/{GroupId}/members/$ref", "", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"{G}groups", $$"""{"id":"{{GroupId}}"}""", HttpStatusCode.Created));
        var group = await writer.CreateSecurityGroupAsync("SAMPLE-GRP-ALPHA-FIN-VIEW", "SAMPLE", CancellationToken.None);
        await writer.AddMemberAsync(group.ObjectId, UserId, CancellationToken.None);

        var body = JsonDocument.Parse(http.Calls[0].Body).RootElement;
        Assert.Equal((false, true, "SAMPLEGRPALPHAFINVIEW"), (body.GetProperty("mailEnabled").GetBoolean(), body.GetProperty("securityEnabled").GetBoolean(), body.GetProperty("mailNickname").GetString()));
        Assert.Equal($"{G}directoryObjects/{AgentOid}", body.GetProperty("owners@odata.bind")[0].GetString());
        Assert.Equal($"{G}groups/{GroupId}/members/$ref", http.Calls[1].Url);
        Assert.Equal($"{G}directoryObjects/{UserId}", JsonDocument.Parse(http.Calls[1].Body).RootElement.GetProperty("@odata.id").GetString());
        Assert.All(http.Calls, c => Assert.Equal(HttpMethod.Post, c.Method));
    }

    [Fact]
    public async Task An_existing_membership_counts_as_done()
    {
        var (writer, _) = Writer(h => h.On(HttpMethod.Post, $"{G}groups/{GroupId}/members/$ref",
            """{"error":{"message":"One or more added object references already exist for the following modified properties: 'members'."}}""", HttpStatusCode.BadRequest));
        await writer.AddMemberAsync(GroupId, UserId, CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}", "GraphForbidden", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"The source resource object or one of the objects being referenced don't exist."}}""", "NotReplicated", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid value"}}""", "GraphError", false)]
    public async Task Errors_become_categories(HttpStatusCode code, string json, string category, bool forbidden)
    {
        var (writer, _) = Writer(h => h.On(HttpMethod.Post, G, json, code));
        var e = await Assert.ThrowsAsync<DirectoryWriteException>(() => writer.AddMemberAsync(GroupId, UserId, CancellationToken.None));
        Assert.Equal((category, forbidden), (e.Category, e.Forbidden));
    }

    [Fact]
    public async Task Ids_that_are_not_guids_never_reach_graph()
    {
        var (writer, http) = Writer(_ => { });
        await Assert.ThrowsAsync<DirectoryWriteException>(() => writer.AddMemberAsync("../users", UserId, CancellationToken.None));
        Assert.Empty(http.Calls);
    }
}

public class ArmRoleAssignmentWriterTests
{
    private const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string GroupId = "22222222-2222-2222-2222-222222222222";

    private static (ArmRoleAssignmentWriter Writer, RouteHandler Http) Writer(Action<RouteHandler> routes)
    {
        var h = new RouteHandler();
        routes(h);
        return (new ArmRoleAssignmentWriter(new HttpClient(h), new StaticCredential(), [Reader]), h);
    }

    [Fact]
    public async Task Puts_one_role_assignment_with_a_name_that_is_the_same_on_every_repeat()
    {
        var (writer, http) = Writer(h => h.On(HttpMethod.Put, $"https://management.azure.com{Scope}/providers/Microsoft.Authorization/roleAssignments/", "{}", HttpStatusCode.Created));
        await writer.AssignAsync(GroupId, "Group", Reader, Scope, CancellationToken.None);
        await writer.AssignAsync(GroupId, "Group", Reader.ToUpperInvariant(), Scope + "/", CancellationToken.None);

        Assert.Equal(2, http.Calls.Count);
        Assert.Equal(http.Calls[0].Url, http.Calls[1].Url); // deterministic assignment name
        Assert.EndsWith("?api-version=2022-04-01", http.Calls[0].Url, StringComparison.Ordinal);
        var p = JsonDocument.Parse(http.Calls[0].Body).RootElement.GetProperty("properties");
        Assert.Equal($"{Scope}/providers/Microsoft.Authorization/roleDefinitions/{Reader}", p.GetProperty("roleDefinitionId").GetString());
        Assert.Equal((GroupId, "Group"), (p.GetProperty("principalId").GetString(), p.GetProperty("principalType").GetString()));
    }

    [Fact]
    public async Task An_existing_assignment_counts_as_done_and_forbidden_is_a_category()
    {
        var (done, _) = Writer(h => h.On(HttpMethod.Put, "https://management.azure.com/", """{"error":{"code":"RoleAssignmentExists"}}""", HttpStatusCode.Conflict));
        await done.AssignAsync(GroupId, "Group", Reader, Scope, CancellationToken.None);
        var (refused, _) = Writer(h => h.On(HttpMethod.Put, "https://management.azure.com/", "{}", HttpStatusCode.Forbidden));
        var e = await Assert.ThrowsAsync<DirectoryWriteException>(() => refused.AssignAsync(GroupId, "Group", Reader, Scope, CancellationToken.None));
        Assert.True(e.Forbidden);
    }

    [Theory]
    [InlineData("8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "Group", Scope)] // Owner: not allow-listed
    [InlineData(Reader, "ServicePrincipal", Scope)]                      // never to a service principal (e.g. itself)
    [InlineData(Reader, "Group", "SAMPLE dbw-alpha-dev")]                // not an Azure scope
    public async Task Anything_outside_the_allow_list_is_refused_before_it_is_sent(string role, string principalType, string scope)
    {
        var (writer, http) = Writer(_ => { });
        var e = await Assert.ThrowsAsync<DirectoryWriteException>(() => writer.AssignAsync(GroupId, principalType, role, scope, CancellationToken.None));
        Assert.Equal("WriteRefused", e.Category);
        Assert.Empty(http.Calls);
    }
}
