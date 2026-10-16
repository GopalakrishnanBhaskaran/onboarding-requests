using System.Globalization;
using System.Security.Cryptography;
using Azure.Core;
using PmoReview.Core.Handoff;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Phase 2c joiner accounts (Graph v1.0): find a user, create a user, assign the licence, issue a Temporary Access Pass.
/// It has no update or delete call. The random initial password is never stored, logged or returned.
/// Needs User.ReadWrite.All, LicenseAssignment.ReadWrite.All and UserAuthenticationMethod.ReadWrite.All.
/// </summary>
public sealed class GraphJoinerDirectory : IJoinerDirectory
{
    private readonly GraphHttp _graph;
    private readonly string _licenseSkuId;

    public GraphJoinerDirectory(HttpClient http, TokenCredential credential, string licenseSkuId)
    {
        if (!Guid.TryParse(licenseSkuId, out var sku)) throw new ArgumentException("ENTRA_LICENSE_SKU_ID must be the licence skuId (GUID).", nameof(licenseSkuId));
        (_graph, _licenseSkuId) = (new GraphHttp(http, credential), sku.ToString("D"));
    }

    public async Task<string?> FindUserAsync(string upn, CancellationToken ct)
    {
        try
        {
            using var doc = await _graph.GetAsync($"{GraphHttp.V1}users/{Uri.EscapeDataString(upn)}?$select=id", "NotFound", ct);
            return GraphHttp.Str(doc.RootElement, "id");
        }
        catch (EntraWriteException e) when (e.Category == "NotFound")
        {
            return null;
        }
    }

    public async Task<string> CreateUserAsync(NewJoinerAccount a, CancellationToken ct)
    {
        using var doc = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.V1}users", new
        {
            accountEnabled = true,
            displayName = a.DisplayName,
            mailNickname = a.MailNickname,
            userPrincipalName = a.UserPrincipalName,
            usageLocation = a.UsageLocation,
            passwordProfile = new { forceChangePasswordNextSignIn = true, password = RandomPassword() },
        }, "JoinerCreateRejected", ct);
        return (doc is null ? null : GraphHttp.Str(doc.RootElement, "id")) ?? throw new EntraWriteException("JoinerCreateRejected");
    }

    public async Task AssignLicenseAsync(string userId, CancellationToken ct)
    {
        using var _ = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.V1}users/{Uri.EscapeDataString(userId)}/assignLicense", new
        {
            addLicenses = new[] { new { skuId = _licenseSkuId, disabledPlans = Array.Empty<string>() } },
            removeLicenses = Array.Empty<string>(),
        }, "LicenseUnavailable", ct);
    }

    public async Task<TemporaryAccessPass> CreateTemporaryAccessPassAsync(string userId, DateTimeOffset start, int lifetimeMinutes, CancellationToken ct)
    {
        using var doc = await _graph.SendAsync(HttpMethod.Post, $"{GraphHttp.V1}users/{Uri.EscapeDataString(userId)}/authentication/temporaryAccessPassMethods", new
        {
            startDateTime = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            lifetimeInMinutes = lifetimeMinutes,
            isUsableOnce = false,
        }, "TapRejected", ct);
        var r = doc?.RootElement ?? throw new EntraWriteException("TapRejected");
        var secret = GraphHttp.Str(r, "temporaryAccessPass") ?? throw new EntraWriteException("TapRejected");
        var from = DateTimeOffset.TryParse(GraphHttp.Str(r, "startDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s) ? s : start;
        var minutes = r.TryGetProperty("lifetimeInMinutes", out var l) && l.TryGetInt32(out var m) ? m : lifetimeMinutes;
        return new TemporaryAccessPass(secret, from, from.AddMinutes(minutes));
    }

    /// <summary>32 characters with upper, lower, digit and symbol. Nobody ever sees it; the joiner signs in with the TAP.</summary>
    public static string RandomPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", symbols = "!@#$%^*-_=+?";
        const string all = upper + lower + digits + symbols;
        var chars = new char[32];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
