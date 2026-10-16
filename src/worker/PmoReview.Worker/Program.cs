using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmoReview.Adapters.Entra;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Handoff;
using PmoReview.Core.Storage;

// Paid component (Phase 2). Refuses to start unless paid services are explicitly allowed (CLAUDE.md rule 10).
string? Env(string name) => Environment.GetEnvironmentVariable(name);
if (!string.Equals(Env("ALLOW_PAID_SERVICES"), "true", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The hand-off sync worker is a paid component. Set ALLOW_PAID_SERVICES=true only in the approved Phase 2 environment.");
if (!Uri.TryCreate(Env("COSMOS_ENDPOINT"), UriKind.Absolute, out var cosmosEndpoint))
    throw new InvalidOperationException("COSMOS_ENDPOINT is required (managed identity; no keys).");

var credential = new Azure.Identity.DefaultAzureCredential();

// Auto-assign (approved 16 Oct 2026): the worker files Entra assignment requests for Queued hand-offs, only on
// access packages in the one allowed catalog and only through the app's own assignment policy.
var autoAssign = string.Equals(Env("ENTRA_AUTO_ASSIGN"), "true", StringComparison.OrdinalIgnoreCase);
if (autoAssign && !Guid.TryParse(Env("ENTRA_ALLOWED_CATALOG_ID"), out _))
    throw new InvalidOperationException("ENTRA_AUTO_ASSIGN=true needs ENTRA_ALLOWED_CATALOG_ID (the PMO catalog's GUID).");

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => CosmosDataStores.Create(
            CosmosDataStores.CreateClientWithIdentity(cosmosEndpoint, credential), Env("COSMOS_DATABASE") ?? "pmoreview"));
        services.AddSingleton<IEntraGovernanceReader>(_ =>
            new GraphEntraGovernanceReader(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, credential));
        services.AddSingleton(sp => new HandoffSync(
            sp.GetRequiredService<DataStores>(), sp.GetRequiredService<IEntraGovernanceReader>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<HandoffSync>(),
            autoAssign
                ? new GraphEntraAssignmentWriter(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, credential,
                    Env("ENTRA_ALLOWED_CATALOG_ID")!, Env("ENTRA_AUTO_ASSIGN_POLICY") ?? "PMO Review auto-assign")
                : null));
    })
    .Build();

host.Run();
