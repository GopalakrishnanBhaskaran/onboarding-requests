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
        try
        {
            var r = await container.ReadItemAsync<T>(id, new PartitionKey(partitionKey), cancellationToken: ct);
            return new Versioned<T>(r.Resource, r.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
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

/// <summary>Container layout. infra/main.bicep must match this exactly.</summary>
public static class CosmosSchema
{
    public const int SharedThroughput = 1000; // free-tier ceiling (VERIFIED_FACTS F13)

    public static readonly (string Name, string PartitionKey, string[][] UniqueKeys)[] Containers =
    [
        (ContainerNames.Projects, "/projectId", []),
        (ContainerNames.RequirementRules, "/projectId", [["/ruleId", "/version"]]),
        (ContainerNames.RoleMappings, "/projectId", [["/mappingId", "/version"]]),
        (ContainerNames.Requests, "/projectId", []),
        (ContainerNames.ReviewRuns, "/projectId", [["/idempotencyKey"]]),
        (ContainerNames.ReviewDecisions, "/projectId", [["/reviewRunId", "/reviewer"]]),
        (ContainerNames.UserAccess, "/userId", []),
        (ContainerNames.Handoffs, "/projectId", []),
        (ContainerNames.JoinerPasses, "/projectId", []),
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
            new CosmosDocumentStore<RequirementRule>(db.GetContainer(ContainerNames.RequirementRules)),
            new CosmosDocumentStore<RoleMapping>(db.GetContainer(ContainerNames.RoleMappings)),
            new CosmosDocumentStore<AccessRequest>(db.GetContainer(ContainerNames.Requests)),
            new CosmosDocumentStore<ReviewRun>(db.GetContainer(ContainerNames.ReviewRuns)),
            new CosmosDocumentStore<ReviewDecision>(db.GetContainer(ContainerNames.ReviewDecisions)),
            new CosmosDocumentStore<UserAccess>(db.GetContainer(ContainerNames.UserAccess)),
            new CosmosDocumentStore<Core.Handoff.EntraHandoff>(db.GetContainer(ContainerNames.Handoffs)),
            new CosmosDocumentStore<Core.Handoff.JoinerPass>(db.GetContainer(ContainerNames.JoinerPasses)));
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
