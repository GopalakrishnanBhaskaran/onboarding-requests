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
