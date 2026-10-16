using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Http;
using PmoReview.Core.Storage;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => PilotSettings.FromEnvironment(Environment.GetEnvironmentVariable));
        services.AddSingleton(sp => CreateStores(sp.GetRequiredService<ILoggerFactory>().CreateLogger("Startup")));
        services.AddSingleton<ApiHost>(sp => PilotApp.Build(
            sp.GetRequiredService<DataStores>(),
            sp.GetRequiredService<PilotSettings>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILoggerFactory>()));
    })
    .Build();

// Resolve once at startup so configuration errors (e.g. paid provider without ALLOW_PAID_SERVICES) fail fast.
host.Services.GetRequiredService<ApiHost>();
host.Run();

static DataStores CreateStores(ILogger log)
{
    string? Env(string name) => Environment.GetEnvironmentVariable(name);
    var mode = Env("STORAGE_MODE") ?? "Cosmos";

    if (string.Equals(mode, "InMemorySample", StringComparison.OrdinalIgnoreCase))
    {
        if (!string.Equals(Env("PILOT_LOCAL_DEV"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("STORAGE_MODE=InMemorySample is only allowed with PILOT_LOCAL_DEV=true.");
        var stores = InMemoryDataStores.Create();
        SampleDataLoader.SeedAsync(stores, SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"))).GetAwaiter().GetResult();
        log.LogWarning("Using in-memory SAMPLE data. Local development only; data is lost on restart.");
        return stores;
    }

    var cs = Env("COSMOS_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException("COSMOS_CONNECTION_STRING app setting is missing.");
    var emulator = string.Equals(Env("COSMOS_EMULATOR"), "true", StringComparison.OrdinalIgnoreCase);
    var database = Env("COSMOS_DATABASE") ?? "pmoreview";
    var client = CosmosDataStores.CreateClient(cs, allowEmulatorCertificate: emulator);
    if (emulator) CosmosDataStores.EnsureCreatedAsync(client, database, CancellationToken.None).GetAwaiter().GetResult();
    return CosmosDataStores.Create(client, database);
}
