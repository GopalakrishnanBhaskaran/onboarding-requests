using System.Text.Json;
using System.Text.RegularExpressions;
using PmoReview.Adapters.Storage;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Keeps infra/ and tools/ in step with the code, the zero-cost rules (CLAUDE.md 1-2) and "the agent never creates or assigns".</summary>
public partial class InfraConsistencyTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PmoReview.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Repo(), .. path]));

    private static string Pilot() => Read("infra", "modules", "pilot.bicep");
    private static string Worker() => Read("infra", "modules", "worker.bicep");
    private static string MainBicep() => Read("infra", "main.bicep");

    [Fact]
    public void Bicep_containers_match_code_schema_and_keep_legacy_containers()
    {
        var declared = ContainerLine().Matches(Pilot()).ToDictionary(
            m => m.Groups["name"].Value,
            m => (Pk: m.Groups["pk"].Value, Keys: string.Join(";", PathGroup().Matches(m.Groups["keys"].Value).Select(k => string.Join(",", Quoted().Matches(k.Value).Select(q => q.Groups[1].Value))))));
        var expected = CosmosSchema.Containers.Concat(CosmosSchema.LegacyContainers).ToList();
        Assert.Equal(expected.Count, declared.Count);
        foreach (var (name, pk, uniqueKeys) in expected)
        {
            Assert.True(declared.TryGetValue(name, out var d), $"container {name} missing from Bicep");
            Assert.Equal(pk, d.Pk);
            Assert.Equal(string.Join(";", uniqueKeys.Select(k => string.Join(",", k))), d.Keys);
        }
        foreach (var v2 in new[] { "mappingSheet", "responseDocument", "notifications", "issues" })
            Assert.Contains(CosmosSchema.Containers, c => c.Name == v2);
    }

    [Fact]
    public void Bicep_uses_only_free_settings_and_allowed_types()
    {
        var bicep = MainBicep() + Pilot();
        Assert.Contains("name: 'Free'", bicep);
        Assert.Contains("enableFreeTier: true", bicep);
        Assert.Contains("throughput: 1000", bicep);
        Assert.Contains("totalThroughputLimit: 1000", bicep);
        Assert.DoesNotContain("autoscaleSettings", bicep, StringComparison.OrdinalIgnoreCase);
        string[] allowed =
        [
            "Microsoft.Resources/resourceGroups", "Microsoft.Authorization/policyAssignments", "Microsoft.Consumption/budgets",
            "Microsoft.Web/staticSites", "Microsoft.DocumentDB/databaseAccounts",
            "Microsoft.DocumentDB/databaseAccounts/sqlDatabases", "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers",
        ];
        var types = ResourceType().Matches(bicep).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.Contains(t, allowed));
    }

    [Fact]
    public void Worker_is_least_privilege_identity_only_and_off_by_default()
    {
        var main = MainBicep();
        var bicep = Worker();
        Assert.Contains("param enableHandoff bool = false", main);
        Assert.Contains("param deployWorker bool = false", main);
        Assert.Contains("module worker 'modules/worker.bicep' = if (enableHandoff && deployWorker)", main);

        string[] allowed =
        [
            "Microsoft.DocumentDB/databaseAccounts", "Microsoft.Storage/storageAccounts", "Microsoft.Storage/storageAccounts/blobServices",
            "Microsoft.Storage/storageAccounts/blobServices/containers", "Microsoft.Web/serverfarms", "Microsoft.Web/sites",
            "Microsoft.Web/sites/basicPublishingCredentialsPolicies", "Microsoft.Authorization/roleAssignments",
            "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments",
        ];
        Assert.All(ResourceType().Matches(bicep).Select(m => m.Groups[1].Value).Distinct(), t => Assert.Contains(t, allowed));
        foreach (var never in new[] { "Microsoft.Insights", "Microsoft.OperationalInsights", "Microsoft.KeyVault", "Microsoft.CognitiveServices", "Microsoft.Network", "Microsoft.ServiceBus", "APPLICATIONINSIGHTS" })
            Assert.DoesNotContain(never, bicep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowSharedKeyAccess: false", bicep);
        Assert.Contains("type: 'SystemAssigned'", bicep);
        Assert.Contains("tier: 'FlexConsumption'", bicep);
        Assert.DoesNotContain("listKeys", bicep);
        Assert.Contains("allow: false", bicep);

        // Cosmos data roles: exactly the containers the agent uses; read-only on projects; never userAccess.
        var writable = Regex.Match(bicep, @"var writableContainers = \[(?<list>[^\]]*)\]").Groups["list"].Value;
        Assert.Equal(["requests", "reviewRuns", "mappingSheet", "responseDocument", "notifications", "issues"],
            Quoted().Matches(writable).Select(m => m.Groups[1].Value).ToArray());
        Assert.Contains("/colls/projects'", bicep);
        Assert.Contains("cosmosBuiltInDataReader", bicep);
        Assert.DoesNotContain("/colls/userAccess", bicep);
        Assert.DoesNotContain("/colls/handoffs", bicep);
        Assert.DoesNotContain("/colls/joinerPasses", bicep);
    }

    [Fact]
    public void Retired_create_and_assign_switches_are_forced_off_and_refused_by_the_worker()
    {
        var bicep = Worker();
        foreach (var s in new[] { "ENTRA_AUTO_ASSIGN", "ENTRA_PROVISION_JOINERS", "ENTRA_AUTO_CREATE_PACKAGES" })
            Assert.Contains($"{{ name: '{s}', value: 'false' }}", bicep);
        Assert.Contains("{ name: 'WORKFLOW_SCHEDULE', value: '0 */5 * * * *' }", bicep);
        var worker = Read("src", "worker", "PmoReview.Worker", "Program.cs");
        Assert.Contains("is retired (Phase 2 access packages)", worker);
        Assert.DoesNotContain("GraphEntraAssignmentWriter", worker);
        Assert.DoesNotContain("JoinerProvisioning", worker);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(Repo(), "src"), "*.cs", SearchOption.AllDirectories),
            f => Regex.IsMatch(File.ReadAllText(f), @"\b(assignmentRequests|assignLicense|temporaryAccessPassMethods|accessPackages|resourceRoleScopes)\b"));
    }

    [Fact]
    public void Graph_permissions_granted_by_the_deploy_script_are_read_only_and_revocation_needs_approval()
    {
        var deploy = Read("tools", "deploy_free.sh");
        var grant = Regex.Match(deploy, @"GRAPH_READ_ROLES=\((?<r>[^)]*)\)").Groups["r"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["User.Read.All", "GroupMember.Read.All", "UserAuthenticationMethod.Read.All", "AuditLog.Read.All"], grant);
        Assert.All(grant, g => Assert.DoesNotContain("Write", g));
        var retired = Regex.Match(deploy, @"GRAPH_RETIRED_ROLES=\((?<r>[^)]*)\)").Groups["r"].Value;
        foreach (var r in new[] { "EntitlementManagement.ReadWrite.All", "User.ReadWrite.All", "LicenseAssignment.ReadWrite.All", "UserAuthenticationMethod.ReadWrite.All", "Group.ReadWrite.All" })
            Assert.Contains(r, retired);
        Assert.Contains("Would remove: $perm (dry run; pass --approved-by after explicit approval)", deploy);
        Assert.DoesNotContain("setup_entra", deploy);
        Assert.False(File.Exists(Path.Combine(Repo(), "tools", "setup_entra_package.ps1")));
    }

    [Fact]
    public void Only_the_two_v2_1_writers_write_with_create_calls_and_only_the_worker_holds_them()
    {
        var adapters = Path.Combine(Repo(), "src", "adapters", "PmoReview.Adapters");
        Assert.Equal(["GraphDirectoryReader.cs", "GraphDirectoryWriter.cs"], Directory.GetFiles(Path.Combine(adapters, "Entra"), "*.cs").Select(Path.GetFileName).Order());
        Assert.Equal(["ArmRoleAssignmentReader.cs", "ArmRoleAssignmentWriter.cs"], Directory.GetFiles(Path.Combine(adapters, "Rbac"), "*.cs").Select(Path.GetFileName).Order());
        foreach (var reader in new[] { Path.Combine(adapters, "Entra", "GraphDirectoryReader.cs"), Path.Combine(adapters, "Rbac", "ArmRoleAssignmentReader.cs") })
            foreach (var m in new[] { "HttpMethod.Patch", "HttpMethod.Delete", "HttpMethod.Put" })
                Assert.DoesNotContain(m, File.ReadAllText(reader));

        // Graph: POST to the three create URLs only; nothing touches authentication methods (the TAP), licences or packages.
        var graph = File.ReadAllText(Path.Combine(adapters, "Entra", "GraphDirectoryWriter.cs"));
        Assert.Single(Regex.Matches(graph, @"HttpMethod\.\w+"));
        Assert.Contains("HttpMethod.Post", graph);
        Assert.Contains("WriteRefused", graph);
        foreach (var banned in new[] { "/authentication", "assignLicense", "accessPackage", "temporaryAccessPass" })
            Assert.DoesNotContain(banned, graph, StringComparison.OrdinalIgnoreCase);

        // Azure RBAC: one PUT of a role assignment; no delete.
        var arm = File.ReadAllText(Path.Combine(adapters, "Rbac", "ArmRoleAssignmentWriter.cs"));
        Assert.Single(Regex.Matches(arm, @"HttpMethod\.\w+"));
        Assert.Contains("HttpMethod.Put", arm);
        Assert.Contains("WriteRefused", arm);

        // The website's API never gets a writer: only the worker composes them, and only when switched on.
        Assert.DoesNotContain("Writer", Read("src", "api", "PmoReview.Api", "Program.cs"));
        var worker = Read("src", "worker", "PmoReview.Worker", "Program.cs");
        Assert.Contains("settings.Workflow.AgentProvisioning ? new GraphDirectoryWriter", worker);
        Assert.Contains("settings.Workflow.AgentRoleAssignment", worker);
    }

    [Fact]
    public void Provisioning_is_off_by_default_and_switched_on_only_with_approval()
    {
        var bicep = MainBicep() + Worker();
        Assert.Contains("param agentProvisioning bool = false", bicep);
        Assert.Contains("param agentRoleAssignment bool = false", bicep);
        Assert.Contains("{ name: 'AGENT_PROVISIONING', value: agentProvisioning ? 'true' : 'false' }", bicep);
        var deploy = Read("tools", "deploy_free.sh");
        var grant = Regex.Match(deploy, @"GRAPH_PROVISION_ROLES=\((?<r>[^)]*)\)").Groups["r"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["User.Create", "Group.Create", "GroupMember.ReadWrite.All"], grant); // least privileged (F56-F58)
        Assert.Contains("Dry run: nothing changed.", deploy);
        Assert.Contains("--condition-version 2.0", deploy);
        Assert.Contains("StringEqualsIgnoreCase $types", deploy);
        Assert.Contains("RBAC_ADMIN_ROLE_ID=f58310d9-a9f6-439a-9e8d-f62e7b41a168", deploy);
    }

    [Fact]
    public void Budget_is_the_approved_cap_with_staged_alerts()
    {
        var bicep = MainBicep() + Pilot();
        Assert.Contains("param budgetAmount int = 2000", bicep);
        foreach (var t in new[] { "threshold: 50", "threshold: 80", "threshold: 100" }) Assert.Contains(t, bicep);
    }

    [Fact]
    public void Worker_target_framework_matches_flex_runtime()
    {
        var csproj = Read("src", "worker", "PmoReview.Worker", "PmoReview.Worker.csproj");
        var tfm = Regex.Match(csproj, "<TargetFramework>net(?<v>[0-9.]+)</TargetFramework>").Groups["v"].Value;
        Assert.Contains($"param workerRuntimeVersion string = '{tfm}'", Worker());
        Assert.Contains($"param workerRuntimeVersion string = '{tfm}'", MainBicep());
    }

    /// <summary>The SWA managed API runtime and the API project must agree (VERIFIED_FACTS F21).</summary>
    [Fact]
    public void Api_target_framework_matches_swa_api_runtime()
    {
        var config = JsonDocument.Parse(Read("staticwebapp.config.json"));
        var runtime = config.RootElement.GetProperty("platform").GetProperty("apiRuntime").GetString()!;
        var tfm = Regex.Match(Read("src", "api", "PmoReview.Api", "PmoReview.Api.csproj"), "<TargetFramework>net(?<v>[0-9.]+)</TargetFramework>").Groups["v"].Value;
        Assert.Equal($"dotnet-isolated:{tfm}", runtime);
    }

    /// <summary>Handoff task 3: sign-out must land on a public page, or the 401 override signs the user straight back in.</summary>
    [Fact]
    public void Sign_out_lands_on_a_public_page_before_the_authenticated_catch_all()
    {
        var config = JsonDocument.Parse(Read("staticwebapp.config.json")).RootElement;
        var routes = config.GetProperty("routes").EnumerateArray().ToList();
        var logout = routes.Single(r => r.GetProperty("route").GetString() == "/logout");
        Assert.Equal("/.auth/logout?post_logout_redirect_uri=/signed-out.html", logout.GetProperty("redirect").GetString());
        var page = routes.FindIndex(r => r.GetProperty("route").GetString() == "/signed-out.html");
        var catchAll = routes.FindIndex(r => r.GetProperty("route").GetString() == "/*");
        Assert.True(page >= 0 && page < catchAll);
        Assert.Equal(["anonymous"], routes[page].GetProperty("allowedRoles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Contains("/signed-out.html", config.GetProperty("navigationFallback").GetProperty("exclude").EnumerateArray().Select(x => x.GetString()));
        var html = Read("src", "web", "public", "signed-out.html");
        Assert.Contains("href=\"/login\"", html);
        Assert.DoesNotContain("<script", html);
    }

    [GeneratedRegex(@"\{ name: '(?<name>\w+)', pk: '(?<pk>[^']+)', uniqueKeys: \[(?<keys>.*?)\](, defaultTtl: -?\d+)? \}( //.*)?$", RegexOptions.Multiline)]
    private static partial Regex ContainerLine();

    [GeneratedRegex(@"paths: \[[^\]]*\]")]
    private static partial Regex PathGroup();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^resource \w+ '([^@']+)@", RegexOptions.Multiline)]
    private static partial Regex ResourceType();
}
