using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Http;
using PmoReview.Core.Security;
using PmoReview.Core.Services;
using PmoReview.Core.Storage;

namespace PmoReview.Adapters;

/// <summary>Settings read from environment / SWA app settings. Defaults are the zero-cost pilot.</summary>
public sealed record PilotSettings
{
    public bool AllowPaidServices { get; init; }
    public IReadOnlyCollection<string> AllowedIdentityProviders { get; init; } = ["aad"];

    public static PilotSettings FromEnvironment(Func<string, string?> get) => new()
    {
        AllowPaidServices = string.Equals(get("ALLOW_PAID_SERVICES"), "true", StringComparison.OrdinalIgnoreCase),
        AllowedIdentityProviders = (get("ALLOWED_IDENTITY_PROVIDERS") ?? "aad")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };
}

/// <summary>Composition root shared by the Functions host and the tests, so tests exercise the real wiring.</summary>
public static class PilotApp
{
    public static ApiHost Build(DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers)
    {
        var host = new ApiHost(new AccessService(stores), settings.AllowedIdentityProviders, loggers.CreateLogger<ApiHost>());
        host.MapCore(stores, new RequestService(stores, clock), new ReferenceDataService(stores));
        return host;
    }
}
