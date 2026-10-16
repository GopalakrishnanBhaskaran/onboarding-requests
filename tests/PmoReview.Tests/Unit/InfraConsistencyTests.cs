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
        Assert.Contains("if (enableHandoff)", main);

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

    [GeneratedRegex(@"\{ name: '(?<name>\w+)', pk: '(?<pk>[^']+)', uniqueKeys: \[(?<keys>.*?)\] \}$", RegexOptions.Multiline)]
    private static partial Regex ContainerLine();

    [GeneratedRegex(@"paths: \[[^\]]*\]")]
    private static partial Regex PathGroup();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^resource \w+ '([^@']+)@", RegexOptions.Multiline)]
    private static partial Regex ResourceType();
}
