using System.Net;
using System.Text.Json;
using PmoReview.Adapters.Databricks;
using PmoReview.Core.Databricks;
using PmoReview.Core.Directory;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>v1.2 Databricks adapter against mocked REST: exact calls (F111, F112, F114, F117), add-only, categories.</summary>
public class DatabricksContractTests
{
    private const string Account = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string Acc = "https://accounts.azuredatabricks.net/api/2.0";
    private const string GroupOid = "22222222-2222-2222-2222-222222222222";
    private const string Group = "azr-pes-rg-data-dev-dbw-weu-datareaders";
    private static readonly DatabricksWorkspace Ws = DatabricksWorkspace.Parse("dbw-pmo=adb-7405607339118600.0.azuredatabricks.net/")!;
    private const string W = "https://adb-7405607339118600.0.azuredatabricks.net/api";

    private static (DatabricksAccessClient Client, RouteHandler Http) Client(Action<RouteHandler> routes)
    {
        var h = new RouteHandler();
        routes(h);
        return (new DatabricksAccessClient(new HttpClient(h), new StaticCredential(), Account), h);
    }

    [Fact]
    public void A_workspace_entry_names_its_id_and_host_and_nothing_else_is_accepted()
    {
        Assert.Equal(("dbw-pmo", 7405607339118600L, "https://adb-7405607339118600.0.azuredatabricks.net/"), (Ws.Name, Ws.Id, Ws.Url.ToString()));
        Assert.Null(DatabricksWorkspace.Parse("evil=https://example.com"));
        Assert.Null(DatabricksWorkspace.Parse("no-host"));
    }

    [Fact]
    public async Task A3_adds_the_entra_group_to_the_account_and_assigns_it_as_user_only()
    {
        var (c, h) = Client(r => r
            .On(HttpMethod.Get, $"{Acc}/identity/accounts/{Account}/external-groups/{GroupOid}", """{"internal_id":"9001","display_name":"x"}""")
            .On(HttpMethod.Get, $"{Acc}/accounts/{Account}/workspaces/7405607339118600/permissionassignments",
                $$"""{"permission_assignments":[{"principal":{"principal_id":9001,"group_name":"{{Group}}"},"permissions":["USER"]}]}""")
            .On(HttpMethod.Put, $"{Acc}/accounts/{Account}/workspaces/7405607339118600/permissionassignments/principals/9001", "{}"));

        Assert.Equal(9001, await c.AddGroupToAccountAsync(GroupOid, default));
        Assert.Equal(["USER"], await c.WorkspacePermissionsAsync(Ws, Group, default));
        Assert.Empty(await c.WorkspacePermissionsAsync(Ws, "another-group", default));
        await c.AssignUserAsync(Ws, 9001, default);
        var put = h.Calls.Single(x => x.Method == HttpMethod.Put);
        Assert.Equal("""{"permissions":["USER"]}""", put.Body);
    }

    [Fact]
    public async Task A4_adds_only_allowed_entitlements_to_the_workspace_group()
    {
        var (c, h) = Client(r => r
            .On(HttpMethod.Get, $"{W}/2.0/preview/scim/v2/Groups?filter=displayName eq \"{Group}\"",
                $$"""{"Resources":[{"id":"123456","displayName":"{{Group}}","entitlements":[{"value":"workspace-access"}]}]}""")
            .On(HttpMethod.Patch, $"{W}/2.0/preview/scim/v2/Groups/123456", "{}"));

        Assert.Equal(["workspace-access"], await c.EntitlementsAsync(Ws, Group, default));
        await c.AddEntitlementsAsync(Ws, Group, ["databricks-sql-access"], default);
        var patch = JsonDocument.Parse(h.Calls.Single(x => x.Method == HttpMethod.Patch).Body).RootElement;
        var op = patch.GetProperty("Operations")[0];
        Assert.Equal(("add", "entitlements", "databricks-sql-access"), (op.GetProperty("op").GetString(), op.GetProperty("path").GetString(), op.GetProperty("value")[0].GetProperty("value").GetString()));
        await Assert.ThrowsAsync<DirectoryWriteException>(() => c.AddEntitlementsAsync(Ws, Group, ["allow-cluster-create"], default));
    }

    [Fact]
    public async Task A_group_the_workspace_does_not_show_reads_as_null()
    {
        var (c, _) = Client(r => r.On(HttpMethod.Get, $"{W}/2.0/preview/scim/v2/Groups", """{"Resources":[]}"""));
        Assert.Null(await c.EntitlementsAsync(Ws, Group, default));
    }

    [Fact]
    public async Task A5_reads_effective_privileges_and_grants_by_adding_never_removing()
    {
        var (c, h) = Client(r => r
            .On(HttpMethod.Get, $"{W}/2.1/unity-catalog/effective-permissions/schema/sample_e2e.curated?principal={Group}",
                $$"""{"privilege_assignments":[{"principal":"{{Group}}","privileges":[{"privilege":"USE_SCHEMA"},{"privilege":"SELECT","inherited_from_type":"CATALOG","inherited_from_name":"sample_e2e"}]}]}""")
            .On(HttpMethod.Patch, $"{W}/2.1/unity-catalog/permissions/schema/sample_e2e.curated", "{}"));

        Assert.Equal(["USE_SCHEMA", "SELECT"], await c.EffectivePrivilegesAsync(Ws, "schema", "sample_e2e.curated", Group, default));
        await c.GrantAsync(Ws, "schema", "sample_e2e.curated", Group, ["MODIFY"], default);
        var change = JsonDocument.Parse(h.Calls.Single(x => x.Method == HttpMethod.Patch).Body).RootElement.GetProperty("changes")[0];
        Assert.Equal((Group, "MODIFY"), (change.GetProperty("principal").GetString(), change.GetProperty("add")[0].GetString()));
        Assert.False(change.TryGetProperty("remove", out _));
    }

    [Theory]
    [InlineData("schema", "main.x;DROP", "SELECT")]
    [InlineData("table", "main.x.t", "SELECT")]
    [InlineData("schema", "main.x", "ALL_PRIVILEGES")]
    [InlineData("schema", "main.x", "MANAGE")]
    public async Task Names_types_and_privileges_outside_the_rules_never_reach_databricks(string type, string name, string privilege)
    {
        var (c, h) = Client(_ => { });
        await Assert.ThrowsAsync<DirectoryWriteException>(() => c.GrantAsync(Ws, type, name, Group, [privilege], default));
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "DatabricksForbidden", true)]
    [InlineData(HttpStatusCode.Unauthorized, "DatabricksForbidden", true)]
    [InlineData(HttpStatusCode.NotFound, "DatabricksNotFound", false)]
    [InlineData(HttpStatusCode.BadRequest, "DatabricksError", false)]
    public async Task Failures_are_categories_and_a_refusal_counts_as_a_missing_right(HttpStatusCode code, string category, bool forbidden)
    {
        var (c, _) = Client(r => r.On(HttpMethod.Patch, $"{W}/2.1/unity-catalog/permissions/catalog/main", """{"message":"secret detail"}""", code));
        var e = await Assert.ThrowsAsync<DirectoryWriteException>(() => c.GrantAsync(Ws, "catalog", "main", Group, ["USE_CATALOG"], default));
        Assert.Equal((category, forbidden), (e.Category, e.Forbidden));
        Assert.DoesNotContain("secret", e.Message);
    }

    [Fact]
    public async Task A_read_failure_is_a_read_exception_so_the_agent_retries()
    {
        var (c, _) = Client(r => r.On(HttpMethod.Get, $"{W}/2.1/unity-catalog/effective-permissions", "{}", HttpStatusCode.ServiceUnavailable));
        var e = await Assert.ThrowsAsync<DirectoryReadException>(() => c.EffectivePrivilegesAsync(Ws, "catalog", "main", Group, default));
        Assert.IsNotType<DirectoryWriteException>(e);
        Assert.Equal("DatabricksUnavailable", e.Category);
    }
}
