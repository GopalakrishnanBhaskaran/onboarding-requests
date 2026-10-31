using System.Text.Json;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Adapters.Storage;

/// <summary>
/// Test and local-dev store with the same semantics as Cosmos: partition-scoped reads,
/// id + unique-key conflicts per partition, ETag checks on replace, deep copies on every read.
/// </summary>
public sealed class InMemoryDocumentStore<T>(params Func<T, string>[] uniqueKeys) : IDocumentStore<T> where T : class, IDocument
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Pk, string Id), (string Json, string ETag)> _docs = [];

    public Task<Versioned<T>?> GetAsync(string partitionKey, string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_docs.TryGetValue((partitionKey, id), out var e) && Readable(e) ? Read(e) : null);
        }
    }

    public Task<IReadOnlyList<Versioned<T>>> ListAsync(string partitionKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Versioned<T>> items = _docs.Where(d => d.Key.Pk == partitionKey && Readable(d.Value)).Select(d => Read(d.Value)).ToList();
            return Task.FromResult(items);
        }
    }

    public Task<IReadOnlyList<Versioned<T>>> ListAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Versioned<T>> items = _docs.Values.Where(Readable).Select(Read).ToList();
            return Task.FromResult(items);
        }
    }

    public Task<Versioned<T>> CreateAsync(T document, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = (document.PartitionKey, document.Id);
            if (_docs.ContainsKey(key)) throw new DocumentConflictException("id exists");
            EnsureUnique(document, except: null);
            var entry = (AppJson.Serialize(document), NewEtag());
            _docs[key] = entry;
            return Task.FromResult(Read(entry));
        }
    }

    public Task<Versioned<T>> ReplaceAsync(T document, string ifMatchETag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = (document.PartitionKey, document.Id);
            if (!_docs.TryGetValue(key, out var current) || current.ETag != ifMatchETag)
                throw new ConcurrencyException("etag mismatch");
            EnsureUnique(document, except: document.Id);
            var entry = (AppJson.Serialize(document), NewEtag());
            _docs[key] = entry;
            return Task.FromResult(Read(entry));
        }
    }

    private void EnsureUnique(T document, string? except)
    {
        foreach (var keyOf in uniqueKeys)
        {
            var value = keyOf(document);
            var clash = _docs
                .Where(d => d.Key.Pk == document.PartitionKey && d.Key.Id != except && Readable(d.Value))
                .Select(d => AppJson.Deserialize<T>(d.Value.Json)!)
                .Any(other => keyOf(other) == value);
            if (clash) throw new DocumentConflictException("unique key exists");
        }
    }

    /// <summary>Tests: stores a document exactly as Cosmos holds it (e.g. a v1 shape this code cannot deserialize).</summary>
    public void PutRaw(string partitionKey, string id, string json)
    {
        lock (_gate) _docs[(partitionKey, id)] = (json, NewEtag());
    }

    private static Versioned<T> Read((string Json, string ETag) e) => new(AppJson.Deserialize<T>(e.Json)!, e.ETag);

    private static bool Readable((string Json, string ETag) e)
    {
        if (T.MinReadableSchemaVersion == 0) return true;
        using var doc = JsonDocument.Parse(e.Json);
        return StoredSchema.Readable<T>(doc.RootElement);
    }

    private static string NewEtag() => $"\"{Guid.NewGuid():n}\"";
}

public static class InMemoryDataStores
{
    /// <summary>Unique keys mirror the Cosmos container definitions in <see cref="CosmosSchema"/>.</summary>
    public static DataStores Create() => new(
        new InMemoryDocumentStore<Project>(),
        new InMemoryDocumentStore<AccessRequest>(),
        new InMemoryDocumentStore<ReviewRun>(r => r.IdempotencyKey),
        new InMemoryDocumentStore<UserAccess>(),
        new InMemoryDocumentStore<MappingRow>(),
        new InMemoryDocumentStore<ResponseRecord>(),
        new InMemoryDocumentStore<Core.Workflow.Notification>(),
        new InMemoryDocumentStore<Core.Workflow.Issue>(),
        new InMemoryDocumentStore<DataVersion>(),
        new InMemoryDocumentStore<Person>(),
        new InMemoryDocumentStore<DataVersion>(),
        new InMemoryDocumentStore<Escalation>(),
        new InMemoryDocumentStore<MappingQualityIssue>(),
        new InMemoryDocumentStore<GroupResolution>(),
        new InMemoryDocumentStore<OffboardingRun>(),
        new InMemoryDocumentStore<AuditEvent>());
}
