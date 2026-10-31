using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;
using CoreJson = PmoReview.Core.Common.AppJson;

namespace PmoReview.Adapters.Storage;

/// <summary>Cosmos DB for NoSQL implementation. Every call is single-partition.</summary>
public sealed class CosmosDocumentStore<T>(Container container) : IDocumentStore<T> where T : class, IDocument
{
    public async Task<Versioned<T>?> GetAsync(string partitionKey, string id, CancellationToken ct = default)
    {
        using var r = await container.ReadItemStreamAsync(id, new PartitionKey(partitionKey), cancellationToken: ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(r.Content, cancellationToken: ct);
        return StoredSchema.Readable<T>(doc.RootElement) ? new Versioned<T>(doc.RootElement.Deserialize<T>(CoreJson.Options)!, r.Headers.ETag) : null;
    }

    public Task<IReadOnlyList<Versioned<T>>> ListAsync(string partitionKey, CancellationToken ct = default) =>
        QueryAsync(new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey), MaxItemCount = 200 }, ct);

    public Task<IReadOnlyList<Versioned<T>>> ListAllAsync(CancellationToken ct = default) =>
        QueryAsync(new QueryRequestOptions { MaxItemCount = 200 }, ct);

    private async Task<IReadOnlyList<Versioned<T>>> QueryAsync(QueryRequestOptions options, CancellationToken ct)
    {
        var results = new List<Versioned<T>>();
        using var it = container.GetItemQueryStreamIterator(new QueryDefinition("SELECT * FROM c"), requestOptions: options);
        while (it.HasMoreResults)
        {
            using var page = await it.ReadNextAsync(ct);
            page.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(page.Content, cancellationToken: ct);
            foreach (var el in doc.RootElement.GetProperty("Documents").EnumerateArray())
            {
                if (!StoredSchema.Readable<T>(el)) continue;
                var etag = el.GetProperty("_etag").GetString() ?? "";
                results.Add(new Versioned<T>(el.Deserialize<T>(CoreJson.Options)!, etag));
            }
        }
        return results;
    }

    public async Task<Versioned<T>> CreateAsync(T document, CancellationToken ct = default)
    {
        try
        {
            var r = await container.CreateItemAsync(document, new PartitionKey(document.PartitionKey), cancellationToken: ct);
            return new Versioned<T>(r.Resource, r.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentConflictException("id or unique key exists");
        }
    }

    public async Task<Versioned<T>> ReplaceAsync(T document, string ifMatchETag, CancellationToken ct = default)
    {
        try
        {
            var r = await container.ReplaceItemAsync(document, document.Id, new PartitionKey(document.PartitionKey),
                new ItemRequestOptions { IfMatchEtag = ifMatchETag }, ct);
            return new Versioned<T>(r.Resource, r.ETag);
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new ConcurrencyException("etag mismatch");
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentConflictException("unique key exists");
        }
    }
}

/// <summary>Skips stored documents older than the type's <see cref="IDocument.MinReadableSchemaVersion"/> (v1, left in place).</summary>
internal static class StoredSchema
{
    public static bool Readable<T>(JsonElement doc) where T : IDocument =>
        T.MinReadableSchemaVersion == 0
        || (doc.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) && n >= T.MinReadableSchemaVersion);
}

/// <summary>
/// Container layout. infra/main.bicep must match this exactly. v1 containers (requirementRules, roleMappings,
/// reviewDecisions, handoffs, joinerPasses) stay in Azure untouched; v2 no longer reads them.
/// </summary>
public static class CosmosSchema
{
    public const int SharedThroughput = 1000; // free-tier ceiling (VERIFIED_FACTS F13)

    public static readonly (string Name, string PartitionKey, string[][] UniqueKeys)[] Containers =
    [
        (ContainerNames.Projects, "/projectId", []),
        (ContainerNames.Requests, "/projectId", []),
        (ContainerNames.ReviewRuns, "/projectId", [["/idempotencyKey"]]),
        (ContainerNames.UserAccess, "/userId", []),
        (ContainerNames.MappingSheet, "/projectId", []),
        (ContainerNames.ResponseDocument, "/projectId", []),
        (ContainerNames.Notifications, "/projectId", []),
        (ContainerNames.Issues, "/projectId", []),
        // v1.1 (30 Oct 2026)
        (ContainerNames.MappingVersions, "/projectId", []),
        (ContainerNames.PeopleInventory, "/projectId", []),
        (ContainerNames.InventoryVersions, "/projectId", []),
        (ContainerNames.Escalations, "/projectId", []),
        (ContainerNames.MappingQualityIssues, "/projectId", []),
        (ContainerNames.GroupResolutions, "/projectId", []),
        (ContainerNames.OffboardingRuns, "/projectId", []),
        (ContainerNames.AuditEvents, "/projectId", []),
    ];

    /// <summary>v1 containers: declared in Bicep so nothing is ever removed, never read or written by v2.</summary>
    public static readonly (string Name, string PartitionKey, string[][] UniqueKeys)[] LegacyContainers =
    [
        ("requirementRules", "/projectId", [["/ruleId", "/version"]]),
        ("roleMappings", "/projectId", [["/mappingId", "/version"]]),
        ("reviewDecisions", "/projectId", [["/reviewRunId", "/reviewer"]]),
        ("handoffs", "/projectId", []),
        ("joinerPasses", "/projectId", []),
    ];
}

public static class CosmosDataStores
{
    private static CosmosClientOptions Options() => new()
    {
        UseSystemTextJsonSerializerWithOptions = CoreJson.Options,
        ApplicationName = "pmo-review-pilot",
    };

    /// <summary>
    /// Paid path (BYO Function App): Entra ID + data-plane RBAC ("Cosmos DB Built-in Data Contributor"),
    /// no keys. Not available to SWA managed Functions (no managed identity, VERIFIED_FACTS F2).
    /// </summary>
    public static CosmosClient CreateClientWithIdentity(Uri endpoint, Azure.Core.TokenCredential credential) =>
        new(endpoint.AbsoluteUri, credential, Options());

    public static CosmosClient CreateClient(string connectionString, bool allowEmulatorCertificate = false)
    {
        var options = Options();
        if (allowEmulatorCertificate)
        {
            // Local emulator only (self-signed cert). Never enabled for a cloud endpoint.
            options.ConnectionMode = ConnectionMode.Gateway;
            options.HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        }
        return new CosmosClient(connectionString, options);
    }

    public static DataStores Create(CosmosClient client, string databaseName)
    {
        var db = client.GetDatabase(databaseName);
        return new DataStores(
            new CosmosDocumentStore<Project>(db.GetContainer(ContainerNames.Projects)),
            new CosmosDocumentStore<AccessRequest>(db.GetContainer(ContainerNames.Requests)),
            new CosmosDocumentStore<ReviewRun>(db.GetContainer(ContainerNames.ReviewRuns)),
            new CosmosDocumentStore<UserAccess>(db.GetContainer(ContainerNames.UserAccess)),
            new CosmosDocumentStore<MappingRow>(db.GetContainer(ContainerNames.MappingSheet)),
            new CosmosDocumentStore<ResponseRecord>(db.GetContainer(ContainerNames.ResponseDocument)),
            new CosmosDocumentStore<Core.Workflow.Notification>(db.GetContainer(ContainerNames.Notifications)),
            new CosmosDocumentStore<Core.Workflow.Issue>(db.GetContainer(ContainerNames.Issues)),
            new CosmosDocumentStore<DataVersion>(db.GetContainer(ContainerNames.MappingVersions)),
            new CosmosDocumentStore<Person>(db.GetContainer(ContainerNames.PeopleInventory)),
            new CosmosDocumentStore<DataVersion>(db.GetContainer(ContainerNames.InventoryVersions)),
            new CosmosDocumentStore<Escalation>(db.GetContainer(ContainerNames.Escalations)),
            new CosmosDocumentStore<MappingQualityIssue>(db.GetContainer(ContainerNames.MappingQualityIssues)),
            new CosmosDocumentStore<GroupResolution>(db.GetContainer(ContainerNames.GroupResolutions)),
            new CosmosDocumentStore<OffboardingRun>(db.GetContainer(ContainerNames.OffboardingRuns)),
            new CosmosDocumentStore<AuditEvent>(db.GetContainer(ContainerNames.AuditEvents)));
    }

    /// <summary>
    /// Creates the database (shared 1000 RU/s) and containers if missing. For the local emulator;
    /// in Azure, infra/main.bicep owns provisioning.
    /// </summary>
    public static async Task EnsureCreatedAsync(CosmosClient client, string databaseName, CancellationToken ct)
    {
        var db = (await client.CreateDatabaseIfNotExistsAsync(databaseName, ThroughputProperties.CreateManualThroughput(CosmosSchema.SharedThroughput), cancellationToken: ct)).Database;
        foreach (var (name, pk, uniqueKeys) in CosmosSchema.Containers)
        {
            var props = new ContainerProperties(name, pk);
            foreach (var paths in uniqueKeys)
            {
                var uk = new UniqueKey();
                foreach (var p in paths) uk.Paths.Add(p);
                props.UniqueKeyPolicy.UniqueKeys.Add(uk);
            }
            await db.CreateContainerIfNotExistsAsync(props, cancellationToken: ct);
        }
    }
}
