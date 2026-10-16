using System.Text.RegularExpressions;
using PmoReview.Adapters.Storage;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Keeps infra/ in step with the code and with the zero-cost rules (CLAUDE.md 1-2).</summary>
public partial class InfraConsistencyTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PmoReview.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Bicep() =>
        File.ReadAllText(Path.Combine(Repo(), "infra", "main.bicep")) + File.ReadAllText(Path.Combine(Repo(), "infra", "modules", "pilot.bicep"));

    private static string HandoffBicep() => File.ReadAllText(Path.Combine(Repo(), "infra", "modules", "handoff.bicep"));

    [Fact]
    public void Bicep_containers_match_code_schema()
    {
        var bicep = File.ReadAllText(Path.Combine(Repo(), "infra", "modules", "pilot.bicep"));
        var declared = ContainerLine().Matches(bicep).ToDictionary(
            m => m.Groups["name"].Value,
            m => (Pk: m.Groups["pk"].Value, Keys: string.Join(";", PathGroup().Matches(m.Groups["keys"].Value).Select(k => string.Join(",", Quoted().Matches(k.Value).Select(q => q.Groups[1].Value))))));

        Assert.Equal(CosmosSchema.Containers.Length, declared.Count);
        foreach (var (name, pk, uniqueKeys) in CosmosSchema.Containers)
        {
            Assert.True(declared.TryGetValue(name, out var d), $"container {name} missing from Bicep");
            Assert.Equal(pk, d.Pk);
            Assert.Equal(string.Join(";", uniqueKeys.Select(k => string.Join(",", k))), d.Keys);
        }
    }

    [Fact]
    public void Bicep_uses_only_free_settings_and_allowed_types()
    {
        var bicep = Bicep();
        Assert.Contains("name: 'Free'", bicep);
        Assert.Contains("enableFreeTier: true", bicep);
        Assert.Contains("throughput: 1000", bicep);
        Assert.Contains("totalThroughputLimit: 1000", bicep);
        Assert.DoesNotContain("autoscaleSettings", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isZoneRedundant: true", bicep);
        Assert.DoesNotContain("enableAnalyticalStorage: true", bicep);

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

    /// <summary>
    /// Phase 2 worker (approved, paid from credits): only the agreed types, identity-only access, least privilege,
    /// and still none of the never-create services (CLAUDE.md rule 2).
    /// </summary>
    [Fact]
    public void Handoff_worker_is_least_privilege_and_off_by_default()
    {
        var main = File.ReadAllText(Path.Combine(Repo(), "infra", "main.bicep"));
        var bicep = HandoffBicep();
        Assert.Contains("param enableHandoff bool = false", main);
        Assert.Contains("param deployWorker bool = false", main);
        Assert.Contains("if (enableHandoff && deployWorker)", main); // two stages: policy first, then the worker

        string[] allowed =
        [
            "Microsoft.DocumentDB/databaseAccounts", "Microsoft.Storage/storageAccounts", "Microsoft.Storage/storageAccounts/blobServices",
            "Microsoft.Storage/storageAccounts/blobServices/containers", "Microsoft.Web/serverfarms", "Microsoft.Web/sites",
            "Microsoft.Web/sites/basicPublishingCredentialsPolicies", "Microsoft.Authorization/roleAssignments",
            "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments",
        ];
        var types = ResourceType().Matches(bicep).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.All(types, t => Assert.Contains(t, allowed));
        foreach (var never in new[] { "Microsoft.Insights", "Microsoft.OperationalInsights", "Microsoft.KeyVault", "Microsoft.CognitiveServices", "Microsoft.Network", "Microsoft.ServiceBus", "APPLICATIONINSIGHTS" })
            Assert.DoesNotContain(never, bicep, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("allowSharedKeyAccess: false", bicep);
        Assert.Contains("type: 'SystemAssigned'", bicep);
        Assert.Contains("tier: 'FlexConsumption'", bicep);
        Assert.Contains("instanceMemoryMB: 512", bicep);
        Assert.Contains("/colls/handoffs'", bicep); // Cosmos data role scoped to the one container it writes
        Assert.DoesNotContain("listKeys", bicep);
        Assert.Contains("allow: false", bicep); // no basic-auth publishing

        // Policy lists every worker type, and only when the hand-off is on.
        var pilot = File.ReadAllText(Path.Combine(Repo(), "infra", "modules", "pilot.bicep"));
        Assert.Contains("enableHandoff ? concat(allowedResourceTypes, handoffResourceTypes) : allowedResourceTypes", pilot);
        foreach (var t in new[] { "Microsoft.Web/serverfarms", "Microsoft.Web/sites", "Microsoft.Storage/storageAccounts", "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments" })
            Assert.Contains($"'{t}'", pilot);
    }

    /// <summary>
    /// Auto-assign (approved 16 Oct 2026) is off by default, pinned to one catalog, and uses the same policy name
    /// in Bicep, the worker default and the admin setup script.
    /// </summary>
    [Fact]
    public void Auto_assign_is_off_by_default_and_pinned_to_one_catalog_and_policy()
    {
        var main = File.ReadAllText(Path.Combine(Repo(), "infra", "main.bicep"));
        var bicep = HandoffBicep();
        Assert.Contains("param enableAutoAssign bool = false", main);
        Assert.Contains("param autoAssign bool = false", bicep);
        Assert.Contains("{ name: 'ENTRA_AUTO_ASSIGN', value: string(autoAssign) }", bicep);
        Assert.Contains("{ name: 'ENTRA_ALLOWED_CATALOG_ID', value: entraCatalogId }", bicep);

        const string policy = "PMO Review auto-assign";
        Assert.Contains($"{{ name: 'ENTRA_AUTO_ASSIGN_POLICY', value: '{policy}' }}", bicep);
        Assert.Contains($"[string] $PolicyName = '{policy}'", File.ReadAllText(Path.Combine(Repo(), "tools", "setup_entra_package.ps1")));
        var worker = File.ReadAllText(Path.Combine(Repo(), "src", "worker", "PmoReview.Worker", "Program.cs"));
        Assert.Contains($"?? \"{policy}\"", worker);
        Assert.Contains("ENTRA_AUTO_ASSIGN=true needs ENTRA_ALLOWED_CATALOG_ID", worker);

        // The admin policy grants no self-requests and no approval stage: the second Reviewer's acceptance is the decision.
        var ps1 = File.ReadAllText(Path.Combine(Repo(), "tools", "setup_entra_package.ps1"));
        Assert.Contains("allowedTargetScope     = 'notSpecified'", ps1);
        Assert.Contains("enableTargetsToSelfAddAccess = $false", ps1);
        // Write permissions are only granted when the worker runs in auto-assign mode.
        var deploy = File.ReadAllText(Path.Combine(Repo(), "tools", "deploy_free.sh"));
        Assert.Contains("[[ \"${auto,,}\" == \"true\" ]] && roles+=(EntitlementManagement.ReadWrite.All User.ReadBasic.All)", deploy);
        Assert.Contains("\"ENTRA_AUTO_ASSIGN=true\" \"REQUIRE_SEPARATE_REVIEWER=true\"", deploy);
    }

    /// <summary>
    /// Phase 2c (approved 16 Oct 2026): joiner accounts and package set-up are off by default, need auto-assign, add
    /// container-scoped Cosmos roles only when on, and grant the extra Graph permissions only when the worker says so.
    /// </summary>
    [Fact]
    public void Phase2c_provisioning_is_off_by_default_and_least_privilege_per_switch()
    {
        var main = File.ReadAllText(Path.Combine(Repo(), "infra", "main.bicep"));
        var bicep = HandoffBicep();
        Assert.Contains("param enableJoinerProvisioning bool = false", main);
        Assert.Contains("param enablePackageAutoCreate bool = false", main);
        Assert.Contains("provisionJoiners: enableAutoAssign && enableJoinerProvisioning", main);
        Assert.Contains("autoCreatePackages: enableAutoAssign && enablePackageAutoCreate", main);
        Assert.Contains("resource cosmosPassWriter 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (provisionJoiners)", bicep);
        Assert.Contains("/colls/joinerPasses'", bicep);
        Assert.Contains("resource cosmosMappingWriter 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (autoCreatePackages)", bicep);
        Assert.Contains("/colls/roleMappings'", bicep);
        Assert.Contains("{ name: 'ENTRA_JOINER_UPN_PREFIX', value: 'sample.' }", bicep);
        Assert.Contains("{ name: 'ENTRA_GROUP_PREFIX', value: 'SAMPLE-GRP-' }", bicep);

        var pilot = File.ReadAllText(Path.Combine(Repo(), "infra", "modules", "pilot.bicep"));
        Assert.Contains("{ name: 'joinerPasses', pk: '/projectId', uniqueKeys: [], defaultTtl: -1 }", pilot);

        var deploy = File.ReadAllText(Path.Combine(Repo(), "tools", "deploy_free.sh"));
        Assert.Contains("[[ \"${prov,,}\" == \"true\" ]] && roles+=(User.ReadWrite.All LicenseAssignment.ReadWrite.All UserAuthenticationMethod.ReadWrite.All)", deploy);
        Assert.Contains("[[ \"${pkgs,,}\" == \"true\" ]] && roles+=(Group.ReadWrite.All)", deploy);

        var worker = File.ReadAllText(Path.Combine(Repo(), "src", "worker", "PmoReview.Worker", "Program.cs"));
        Assert.Contains("var provisionJoiners = autoAssign &&", worker);
        Assert.Contains("var autoCreatePackages = autoAssign &&", worker);
    }

    [Fact]
    public void Budget_is_the_approved_phase2_cap_with_staged_alerts()
    {
        var bicep = Bicep();
        Assert.Contains("param budgetAmount int = 2000", bicep);
        foreach (var t in new[] { "threshold: 50", "threshold: 80", "threshold: 100" }) Assert.Contains(t, bicep);
        Assert.Contains("thresholdType: 'Forecasted'", bicep);
    }

    [Fact]
    public void Worker_target_framework_matches_flex_runtime()
    {
        var csproj = File.ReadAllText(Path.Combine(Repo(), "src", "worker", "PmoReview.Worker", "PmoReview.Worker.csproj"));
        var tfm = Regex.Match(csproj, "<TargetFramework>net(?<v>[0-9.]+)</TargetFramework>").Groups["v"].Value;
        Assert.Contains($"param workerRuntimeVersion string = '{tfm}'", HandoffBicep());
        Assert.Contains($"param workerRuntimeVersion string = '{tfm}'", File.ReadAllText(Path.Combine(Repo(), "infra", "main.bicep")));
    }

    /// <summary>
    /// The SWA managed API runtime and the API project must agree, and the runtime must be one the SWA
    /// deploy tooling accepts (VERIFIED_FACTS F21: dotnet-isolated:10.0 is rejected as of 16 Oct 2026).
    /// </summary>
    [Fact]
    public void Api_target_framework_matches_swa_api_runtime()
    {
        var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "staticwebapp.config.json")));
        var runtime = config.RootElement.GetProperty("platform").GetProperty("apiRuntime").GetString()!;
        var csproj = File.ReadAllText(Path.Combine(Repo(), "src", "api", "PmoReview.Api", "PmoReview.Api.csproj"));
        var tfm = Regex.Match(csproj, "<TargetFramework>net(?<v>[0-9.]+)</TargetFramework>").Groups["v"].Value;

        Assert.Equal($"dotnet-isolated:{tfm}", runtime);
        Assert.Contains(runtime, new[] { "dotnet-isolated:8.0", "dotnet-isolated:9.0", "dotnet-isolated:10.0" });
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
