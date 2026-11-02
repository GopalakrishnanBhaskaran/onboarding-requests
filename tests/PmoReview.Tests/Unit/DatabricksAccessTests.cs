using PmoReview.Core.Databricks;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Databricks access by Entra group name (Gokul, 2 Nov 2026; config/databricks-access.json).</summary>
public class DatabricksAccessTests
{
    private static readonly DatabricksPolicy P = DatabricksPolicy.Default;

    [Fact]
    public void A_reader_group_gets_workspace_access_and_databricks_sql_and_read_privileges()
    {
        var d = P.Decide("azr-pes-rg-data-dev-dbw-weu-datareaders");
        Assert.True(d.ByAgent);
        Assert.Equal("Reader", d.Level);
        Assert.Equal(["workspace-access", "databricks-sql-access"], d.Entitlements);
        Assert.Equal(["USE_CATALOG"], d.CatalogPrivileges);
        Assert.Equal(["USE_SCHEMA", "SELECT"], d.SchemaPrivileges);
    }

    [Theory]
    [InlineData("azr-pes-rg-data-dev-ph-weu-dataengineers", "Engineer")]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-datawriters", "Writer")]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-readwrite", "Writer")]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-support-reader", "Reader")]
    [InlineData("SAMPLE_GRP_DBX_VIEWERS", "Reader")]
    public void Workspace_access_is_given_to_every_level_and_sql_access_only_to_readers(string group, string level)
    {
        var d = P.Decide(group);
        Assert.True(d.ByAgent, d.Why);
        Assert.Equal(level, d.Level);
        Assert.Contains("workspace-access", d.Entitlements);
        Assert.Equal(level == "Reader", d.Entitlements.Contains("databricks-sql-access"));
        Assert.Contains("SELECT", d.SchemaPrivileges);
        Assert.Equal(level != "Reader", d.SchemaPrivileges.Contains("MODIFY"));
    }

    [Theory]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-admins", "Admin")]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-dataowner", "Admin")]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-readers-admin", "Admin")] // an admin part anywhere refuses
    [InlineData("azr-pes-rg-data-dev-dbw-weu-analysts", "Unknown")]
    [InlineData("azr-pes-rg-data-dev-dbw-weu-readers-engineers", "Ambiguous")]
    public void Admin_unknown_or_ambiguous_names_are_left_to_a_person(string group, string level)
    {
        var d = P.Decide(group);
        Assert.False(d.ByAgent);
        Assert.Equal(level, d.Level);
        Assert.Empty(d.Entitlements);
        Assert.Empty(d.CatalogPrivileges.Concat(d.SchemaPrivileges));
        Assert.False(string.IsNullOrWhiteSpace(d.Why));
    }

    [Fact]
    public void The_policy_never_gives_admin_privileges_or_cluster_creation()
    {
        var all = P.Levels.SelectMany(l => l.CatalogPrivileges.Concat(l.SchemaPrivileges)).ToList();
        Assert.DoesNotContain("ALL_PRIVILEGES", all);
        Assert.DoesNotContain("MANAGE", all);
        Assert.DoesNotContain(P.Levels.SelectMany(l => l.Entitlements).Concat(P.EntitlementsForEveryone), e => e.StartsWith("allow-", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => DatabricksPolicy.Parse(
            """{"entitlementsForEveryone":["workspace-access"],"levels":[{"level":"X","match":["x"],"byAgent":true,"schemaPrivileges":["ALL_PRIVILEGES"]}]}"""));
        Assert.Throws<InvalidOperationException>(() => DatabricksPolicy.Parse(
            """{"entitlementsForEveryone":["allow-cluster-create"],"levels":[{"level":"X","match":["x"],"byAgent":true,"schemaPrivileges":["SELECT"]}]}"""));
    }

    [Fact]
    public void The_policy_file_is_the_one_the_worker_embeds()
    {
        var file = File.ReadAllText(Path.Combine(Root(), "config", "databricks-access.json"));
        var parsed = DatabricksPolicy.Parse(file);
        Assert.Equal(parsed.Levels.Select(l => (l.Name, string.Join(',', l.Match), l.ByAgent)), P.Levels.Select(l => (l.Name, string.Join(',', l.Match), l.ByAgent)));
    }

    [Theory]
    [InlineData("dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated", "dbw-pmo-pkt2ofpqmlf3e", "sample_e2e", "curated", "schema", "sample_e2e.curated")]
    [InlineData(" DBX:ws1/main ", "ws1", "main", null, "catalog", "main")]
    public void A_databricks_role_scope_names_the_workspace_catalog_and_schema(string scope, string ws, string catalog, string? schema, string type, string full)
    {
        var t = DatabricksPolicy.ParseTarget(scope)!;
        Assert.Equal((ws, catalog, schema), (t.Workspace, t.Catalog, t.Schema));
        Assert.Equal((type, full), t.SchemaSecurable);
        Assert.True(DatabricksPolicy.IsDatabricksScope(scope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/subscriptions/s/resourceGroups/rg")]
    [InlineData("dbx:ws")]                         // no catalog
    [InlineData("dbx:ws/a.b.c")]                   // a table is not a target
    [InlineData("dbx:ws/main;DROP")]               // not a Unity Catalog name
    [InlineData("dbx:../x/main")]                  // not a workspace name
    public void Anything_else_is_not_a_databricks_target(string? scope) => Assert.Null(DatabricksPolicy.ParseTarget(scope));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PmoReview.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
