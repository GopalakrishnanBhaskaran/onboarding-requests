using System.Collections.Concurrent;
using System.Text.Json;
using PmoReview.Core.Common;
using PmoReview.Core.Uploads;

namespace PmoReview.Adapters.Uploads;

/// <summary>
/// F4 external upload API: the token signing keys of a tenant, read from its v2.0 OpenID metadata
/// (<c>https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration</c> → <c>jwks_uri</c>, F99). Anonymous
/// public documents (no credential). Cached for 6 hours; a token naming an unknown key reads them again at most every
/// 5 minutes (key rollover). Only <c>https://login.microsoftonline.com/</c> URLs are followed.
/// </summary>
public sealed class OpenIdSigningKeys(HttpClient http, IClock clock) : ISigningKeys
{
    private const string Authority = "https://login.microsoftonline.com/";
    private static readonly TimeSpan Keep = TimeSpan.FromHours(6);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<SigningKey> Keys)> cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SigningKey?> FindAsync(string tenantId, string kid, CancellationToken ct)
    {
        if (!Guid.TryParse(tenantId, out var tenant)) return null;
        var now = clock.UtcNow;
        if (cache.TryGetValue(tenantId, out var hit) && now - hit.At < Keep)
        {
            var known = hit.Keys.FirstOrDefault(k => k.Kid == kid);
            if (known is not null || now - hit.At < Retry) return known;
        }
        var keys = await ReadAsync(tenant.ToString("D"), ct);
        cache[tenantId] = (now, keys);
        return keys.FirstOrDefault(k => k.Kid == kid);
    }

    private async Task<IReadOnlyList<SigningKey>> ReadAsync(string tenant, CancellationToken ct)
    {
        try
        {
            using var meta = JsonDocument.Parse(await http.GetStringAsync($"{Authority}{tenant}/v2.0/.well-known/openid-configuration", ct));
            var jwks = meta.RootElement.TryGetProperty("jwks_uri", out var u) ? u.GetString() : null;
            if (jwks is null || !jwks.StartsWith(Authority, StringComparison.Ordinal)) return [];
            using var doc = JsonDocument.Parse(await http.GetStringAsync(jwks, ct));
            if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return [];
            return keys.EnumerateArray()
                .Where(k => Str(k, "kty") == "RSA" && Str(k, "use") is null or "sig" && Str(k, "kid") is not null && Str(k, "n") is not null && Str(k, "e") is not null)
                .Select(k => new SigningKey(Str(k, "kid")!, UploadTokenValidator.B64(Str(k, "n")!), UploadTokenValidator.B64(Str(k, "e")!)))
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException && !ct.IsCancellationRequested)
        {
            return []; // no keys = no token is accepted (fails closed)
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
