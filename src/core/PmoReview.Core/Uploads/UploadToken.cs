using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PmoReview.Core.Common;

namespace PmoReview.Core.Uploads;

/// <summary>An RSA signing key of the tenant (JWKS entry: kid, modulus, exponent).</summary>
public sealed record SigningKey(string Kid, byte[] Modulus, byte[] Exponent);

/// <summary>
/// The tenant's token signing keys from its v2.0 OpenID metadata (F99). The adapter caches them and reads them again when a
/// token names a key it does not know (key rollover).
/// </summary>
public interface ISigningKeys
{
    Task<SigningKey?> FindAsync(string tenantId, string kid, CancellationToken ct);
}

/// <summary>The calling application, as its validated token says.</summary>
public sealed record AppCaller(string TenantId, string AppId);

/// <summary>
/// F4 external upload API: validates the Entra access token of a client-credentials caller (F99). Only v2.0 tokens: RS256
/// signature by a key of the token's own tenant (<c>kid</c>), issuer <c>https://login.microsoftonline.com/{tid}/v2.0</c>, audience
/// = this API's client id, <c>tid</c> a GUID, inside its lifetime (5 minutes skew), app-only (no <c>scp</c>), and the app role
/// <see cref="Role"/>. Identity comes only from here (R7); the body never names a tenant, project or user.
/// </summary>
/// <param name="tenantKnown">
/// A tenant a project of this app names. Asked before any signing key is fetched, so an anonymous caller never makes the
/// API read OpenID metadata for an arbitrary tenant (review finding 13); an unknown tenant is just an invalid token.
/// </param>
public sealed class UploadTokenValidator(ISigningKeys keys, string? audience, IClock clock, Func<string, CancellationToken, Task<bool>>? tenantKnown = null)
{
    public const string Role = "Uploads.Write";
    private const int MaxTokenChars = 16 * 1024;
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public async Task<AppCaller> ValidateAsync(string? authorization, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(audience) || !Guid.TryParse(audience, out _)) throw Invalid(); // not configured: nobody gets in
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var token = authorization[7..].Trim();
        if (token.Length is 0 or > MaxTokenChars) throw Invalid();
        var parts = token.Split('.');
        if (parts.Length != 3) throw Invalid();

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(B64(parts[0])).RootElement;
            payload = JsonDocument.Parse(B64(parts[1])).RootElement;
            signature = B64(parts[2]);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw Invalid();
        }
        if (Str(header, "alg") != "RS256" || Str(header, "kid") is not { Length: > 0 } kid) throw Invalid();
        if (Str(payload, "ver") != "2.0") throw Invalid();
        if (Str(payload, "tid") is not { } tid || !Guid.TryParse(tid, out var tenant) || tid != tenant.ToString("D")) throw Invalid();
        if (Str(payload, "iss") != $"https://login.microsoftonline.com/{tid}/v2.0") throw Invalid();
        if (!string.Equals(Str(payload, "aud"), audience, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var now = clock.UtcNow;
        if (Num(payload, "exp") is not { } exp || DateTimeOffset.FromUnixTimeSeconds(exp) + Skew < now) throw Invalid();
        if (Num(payload, "nbf") is { } nbf && DateTimeOffset.FromUnixTimeSeconds(nbf) - Skew > now) throw Invalid();
        if (payload.TryGetProperty("scp", out _)) throw Invalid(); // a user's delegated token, not an application
        if (Str(payload, "idtyp") is { } idtyp && idtyp != "app") throw Invalid();

        if (tenantKnown is not null && !await tenantKnown(tid, ct)) throw Invalid();
        var key = await keys.FindAsync(tid, kid, ct) ?? throw Invalid();
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw Invalid();

        var roles = payload.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList()
            : [];
        if (!roles.Contains(Role, StringComparer.Ordinal))
            throw new AppException(ErrorKind.Forbidden, "missing_app_role", $"The calling application has no {Role} app role for this API.");
        return new AppCaller(tid, Str(payload, "azp") ?? Str(payload, "appid") ?? "unknown");
    }

    private static AppException Invalid() => new(ErrorKind.Unauthenticated, "invalid_token", "A valid Entra access token for this API is required.");

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public static byte[] B64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
