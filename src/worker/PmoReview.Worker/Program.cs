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

// Phase 2c (approved 16 Oct 2026): create joiner accounts (licence + Temporary Access Pass) and set up Entra packages
// for published mappings. Both need auto-assign; each has its own switch.
var provisionJoiners = autoAssign && string.Equals(Env("ENTRA_PROVISION_JOINERS"), "true", StringComparison.OrdinalIgnoreCase);
var autoCreatePackages = autoAssign && string.Equals(Env("ENTRA_AUTO_CREATE_PACKAGES"), "true", StringComparison.OrdinalIgnoreCase);
var tenantDomain = Env("ENTRA_TENANT_DOMAIN") ?? "";
if ((provisionJoiners || autoCreatePackages) && !tenantDomain.Contains('.'))
    throw new InvalidOperationException("ENTRA_PROVISION_JOINERS / ENTRA_AUTO_CREATE_PACKAGES need ENTRA_TENANT_DOMAIN (e.g. contoso.onmicrosoft.com).");
if (provisionJoiners && !Guid.TryParse(Env("ENTRA_LICENSE_SKU_ID"), out _))
    throw new InvalidOperationException("ENTRA_PROVISION_JOINERS=true needs ENTRA_LICENSE_SKU_ID (the Entra ID P2 skuId).");
var policyName = Env("ENTRA_AUTO_ASSIGN_POLICY") ?? "PMO Review auto-assign";
HttpClient Graph() => new() { Timeout = TimeSpan.FromSeconds(30) };

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
            autoAssign ? new GraphEntraAssignmentWriter(Graph(), credential, Env("ENTRA_ALLOWED_CATALOG_ID")!, policyName) : null,
            provisionJoiners
                ? new JoinerProvisioning(new GraphJoinerDirectory(Graph(), credential, Env("ENTRA_LICENSE_SKU_ID")!),
                    new JoinerProvisioningSettings
                    {
                        TenantDomain = tenantDomain,
                        UpnPrefix = Env("ENTRA_JOINER_UPN_PREFIX") ?? "sample.",
                        TapLifetimeMinutes = int.TryParse(Env("ENTRA_TAP_LIFETIME_MINUTES"), out var tl) ? tl : 480,
                    },
                    sp.GetRequiredService<DataStores>(), sp.GetRequiredService<IClock>())
                : null));
        if (autoCreatePackages)
            services.AddSingleton(sp => new PackageSync(sp.GetRequiredService<DataStores>(),
                new GraphPackageProvisioner(Graph(), credential, Env("ENTRA_ALLOWED_CATALOG_ID")!, policyName),
                tenantDomain, Env("ENTRA_GROUP_PREFIX") ?? "SAMPLE-GRP-", sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<PackageSync>()));
    })
    .Build();

host.Run();
