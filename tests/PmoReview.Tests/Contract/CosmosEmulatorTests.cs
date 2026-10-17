using System.Text;
using Microsoft.Azure.Cosmos;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>
/// Opt-in: the whole flow on a real Cosmos DB (the Linux emulator) next to v1 documents in the shared containers, as in the
/// live account. The in-memory store cannot show Cosmos-only behaviour (17 Oct 2026: v1 review runs broke the live submit).
/// Runs when PMO_COSMOS_EMULATOR holds the emulator connection string (its well-known key, from Microsoft Learn); skipped otherwise.
/// <c>docker run -d -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview</c>
/// </summary>
public class CosmosEmulatorTests
{
    [Fact]
    public async Task Full_flow_runs_on_cosmos_next_to_legacy_v1_documents()
    {
        var cs = Environment.GetEnvironmentVariable("PMO_COSMOS_EMULATOR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(cs), "PMO_COSMOS_EMULATOR is not set (Cosmos emulator connection string).");
        var client = CosmosDataStores.CreateClient(cs!, allowEmulatorCertificate: true);
        var database = $"pmoreview-test-{Guid.NewGuid():n}"[..24];
        await CosmosDataStores.EnsureCreatedAsync(client, database, CancellationToken.None);
        try
        {
            var db = client.GetDatabase(database);
            await PutRaw(db.GetContainer("requests"), LegacyV1.Request);
            await PutRaw(db.GetContainer("reviewRuns"), LegacyV1.ReviewRun);
            var app = TestApp.On(CosmosDataStores.Create(client, database));

            var id = await app.To(RequestStatus.Verifying);
            app.UserCompletesFirstSignIn();
            await app.RunWorker();
            Assert.Equal(RequestStatus.Completed, await app.Status(id));

            var alpha = $"projects/{TestApp.Alpha}";
            foreach (var (user, path) in new[]
            {
                (TestApp.Alice, $"{alpha}/requests"), (TestApp.Alice, $"{alpha}/requests/{LegacyV1.RequestId}"),
                (TestApp.Alice, $"{alpha}/requests/{LegacyV1.RequestId}/trail"), (TestApp.Alice, $"{alpha}/requests/{id}/trail"),
                (TestApp.Alice, "queue"), (TestApp.Paul, $"{alpha}/mapping-sheet"), (TestApp.Paul, $"{alpha}/response-document"),
                (TestApp.Oscar, $"{alpha}/issues"), (TestApp.Oscar, $"{alpha}/reports/daily/{app.Clock.UtcNow:yyyy-MM-dd}"),
                (TestApp.Oscar, $"{alpha}/reports/developer"),
            })
                Assert.True((await app.Call(user, "GET", path)).Status == 200, path);
            Assert.Null((await app.Request(LegacyV1.RequestId)).Flow); // still read-only legacy
        }
        finally
        {
            await client.GetDatabase(database).DeleteAsync();
        }
    }

    private static async Task PutRaw(Container container, string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json).RootElement;
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var r = await container.UpsertItemStreamAsync(body, new PartitionKey(doc.GetProperty("projectId").GetString()));
        r.EnsureSuccessStatusCode();
    }
}
