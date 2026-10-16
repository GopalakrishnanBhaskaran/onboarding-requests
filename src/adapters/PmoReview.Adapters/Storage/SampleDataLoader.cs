using System.Text.Json;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Adapters.Storage;

/// <summary>Loads tools/sample_data/sample-data.json. Refuses any record not marked isSample.</summary>
public static class SampleDataLoader
{
    public sealed record SampleData(
        List<Project> Projects,
        List<RequirementRule> RequirementRules,
        List<RoleMapping> RoleMappings,
        List<UserAccess> UserAccess);

    public static SampleData Read(string path)
    {
        var data = JsonSerializer.Deserialize<SampleData>(File.ReadAllText(path), AppJson.Options)
                   ?? throw new InvalidOperationException("Sample data file is empty.");
        var notSample = data.Projects.Any(p => !p.IsSample) || data.RequirementRules.Any(r => !r.IsSample)
                        || data.RoleMappings.Any(m => !m.IsSample) || data.UserAccess.Any(u => !u.IsSample);
        if (notSample) throw new InvalidOperationException("Sample data contains a record without isSample=true. Refusing to load.");
        return data;
    }

    public static async Task SeedAsync(DataStores stores, SampleData data, CancellationToken ct = default)
    {
        foreach (var p in data.Projects) await Upsert(stores.Projects, p, ct);
        foreach (var r in data.RequirementRules) await Upsert(stores.Rules, r, ct);
        foreach (var m in data.RoleMappings) await Upsert(stores.Mappings, m, ct);
        foreach (var u in data.UserAccess) await Upsert(stores.UserAccess, u, ct);
    }

    private static async Task Upsert<T>(IDocumentStore<T> store, T doc, CancellationToken ct) where T : class, IDocument
    {
        var existing = await store.GetAsync(doc.PartitionKey, doc.Id, ct);
        if (existing is null) await store.CreateAsync(doc, ct);
        else await store.ReplaceAsync(doc, existing.ETag, ct);
    }
}
