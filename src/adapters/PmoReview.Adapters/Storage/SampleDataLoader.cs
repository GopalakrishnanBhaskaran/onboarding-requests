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
        List<MappingRow> MappingSheet,
        List<ResponseRecord> ResponseDocument,
        List<UserAccess> UserAccess)
    {
        /// <summary>v1.1: SAMPLE people inventory (TAP recipient tests). Optional in older sample files.</summary>
        public List<Person> PeopleInventory { get; init; } = [];
    }

    /// <summary>Written in the sample file where the Azure scope is the approved access resource group (seed --access-scope).</summary>
    public const string AccessScopePlaceholder = "{{ACCESS_SCOPE}}";

    /// <param name="accessScope">Fills <see cref="AccessScopePlaceholder"/>; null leaves those scopes unresolved (role team).</param>
    public static SampleData Read(string path, string? accessScope = null)
    {
        var data = JsonSerializer.Deserialize<SampleData>(File.ReadAllText(path), AppJson.Options)
                   ?? throw new InvalidOperationException("Sample data file is empty.");
        data = data with
        {
            MappingSheet = data.MappingSheet.Select(r => r with
            {
                AzureRoles = r.AzureRoles.Select(g => g.Scope == AccessScopePlaceholder ? g with { Scope = accessScope } : g).ToList(),
            }).ToList(),
        };
        var notSample = data.Projects.Any(p => !p.IsSample) || data.MappingSheet.Any(r => !r.IsSample)
                        || data.ResponseDocument.Any(r => !r.IsSample) || data.UserAccess.Any(u => !u.IsSample)
                        || data.PeopleInventory.Any(p => !p.IsSample);
        if (notSample) throw new InvalidOperationException("Sample data contains a record without isSample=true. Refusing to load.");
        return data;
    }

    public static async Task SeedAsync(DataStores stores, SampleData data, CancellationToken ct = default)
    {
        foreach (var p in data.Projects) await Upsert(stores.Projects, p, ct);
        foreach (var r in data.MappingSheet) await Upsert(stores.MappingSheet, r, ct);
        foreach (var r in data.ResponseDocument) await Upsert(stores.ResponseDocument, r, ct);
        foreach (var u in data.UserAccess) await Upsert(stores.UserAccess, u, ct);
        foreach (var p in data.PeopleInventory) await Upsert(stores.PeopleInventory, p, ct);
    }

    private static async Task Upsert<T>(IDocumentStore<T> store, T doc, CancellationToken ct) where T : class, IDocument
    {
        var existing = await store.GetAsync(doc.PartitionKey, doc.Id, ct);
        if (existing is null) await store.CreateAsync(doc, ct);
        else await store.ReplaceAsync(doc, existing.ETag, ct);
    }
}
