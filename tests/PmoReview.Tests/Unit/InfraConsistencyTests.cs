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

    [GeneratedRegex(@"\{ name: '(?<name>\w+)', pk: '(?<pk>[^']+)', uniqueKeys: \[(?<keys>.*?)\] \}$", RegexOptions.Multiline)]
    private static partial Regex ContainerLine();

    [GeneratedRegex(@"paths: \[[^\]]*\]")]
    private static partial Regex PathGroup();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^resource \w+ '([^@']+)@", RegexOptions.Multiline)]
    private static partial Regex ResourceType();
}
