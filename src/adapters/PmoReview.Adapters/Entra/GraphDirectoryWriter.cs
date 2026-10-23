using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Microsoft Graph v1.0, agent provisioning (v2.1, worker only, after both approvals). Exactly four create calls:
/// <c>POST /users</c> (User.Create), <c>POST /groups</c> (Group.Create, owned by the agent's own identity),
/// <c>POST /groups/{id}/members/$ref</c> (GroupMember.ReadWrite.All) — VERIFIED_FACTS F56–F58 — and, with the agent TAP
/// on, <c>POST /users/{id}/authentication/temporaryAccessPassMethods</c> (UserAuthMethod-TAP.ReadWrite.All, F66). No
/// update, delete or licence call exists here; any other URL is refused before it is sent. Passwords and passes are
/// <see cref="Secret"/>s: never logged, and Graph error text is only matched, never kept.
/// </summary>
public sealed class GraphDirectoryWriter(HttpClient http, TokenCredential credential, TimeSpan? replicationDelay = null) : IDirectoryWriter
{
    private const string V1 = "https://graph.microsoft.com/v1.0/";
    /// <summary>Application permissions that allow creating a TAP (F66): the narrow one the deploy script grants, or the broad one.</summary>
    private static readonly string[] TapRoles = ["UserAuthMethod-TAP.ReadWrite.All", "UserAuthenticationMethod.ReadWrite.All"];
    private readonly TimeSpan replicationDelay = replicationDelay ?? TimeSpan.FromSeconds(3);

    public async Task<DirectoryUser> CreateUserAsync(NewUser user, Secret initialPassword, CancellationToken ct)
    {
        // Entra requires an initial password. The user must change it at the first password sign-in. Without the agent
        // TAP it is never shown to anyone (the user signs in with the ID team's TAP).
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["accountEnabled"] = true,
            ["displayName"] = user.DisplayName,
            ["givenName"] = user.GivenName,
            ["surname"] = user.Surname,
            ["mailNickname"] = user.MailNickname,
            ["userPrincipalName"] = user.UserPrincipalName,
            ["passwordProfile"] = new Dictionary<string, object> { ["forceChangePasswordNextSignIn"] = true, ["password"] = initialPassword.Reveal() },
        });
        using var doc = await PostAsync($"{V1}users", body, ct) ?? throw new DirectoryWriteException("GraphError");
        return new DirectoryUser(Str(doc.RootElement, "id") ?? throw new DirectoryWriteException("GraphError"), user.UserPrincipalName, "Member", true);
    }

    public async Task<bool> CanIssueTapAsync(CancellationToken ct)
    {
        using var claims = await ClaimsAsync(ct);
        return claims.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            && roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && TapRoles.Contains(r.GetString(), StringComparer.Ordinal));
    }

    public async Task<IssuedTap> CreateTemporaryAccessPassAsync(string userObjectId, int lifetimeMinutes, CancellationToken ct)
    {
        // One-time, starts now. A user has at most one TAP: a new one replaces a valid one (F66).
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["lifetimeInMinutes"] = lifetimeMinutes, ["isUsableOnce"] = true });
        using var doc = await WhenReplicatedAsync(() => PostAsync($"{V1}users/{Guid(userObjectId)}{TapSuffix}", body, ct), ct)
                        ?? throw new DirectoryWriteException("GraphError");
        var e = doc.RootElement;
        var pass = Str(e, "temporaryAccessPass");
        if (string.IsNullOrEmpty(pass)) throw new DirectoryWriteException("GraphError");
        var start = Time(e, "startDateTime") ?? Time(e, "createdDateTime") ?? DateTimeOffset.UtcNow;
        var minutes = e.TryGetProperty("lifetimeInMinutes", out var l) && l.TryGetInt32(out var m) ? m : lifetimeMinutes;
        var once = !e.TryGetProperty("isUsableOnce", out var o) || o.ValueKind != JsonValueKind.False;
        return new IssuedTap(new Secret(pass), start, start.AddMinutes(minutes), once);
    }

    public async Task<DirectoryGroup> CreateSecurityGroupAsync(string displayName, string description, CancellationToken ct)
    {
        var owner = await OwnObjectIdAsync(ct);
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["description"] = description,
            ["mailEnabled"] = false,
            ["mailNickname"] = Nickname(displayName),
            ["securityEnabled"] = true,
            // Group.Create without an owner makes an unmodifiable group (F57): the agent's own identity owns it.
            ["owners@odata.bind"] = new[] { $"{V1}directoryObjects/{owner}" },
        });
        using var doc = await PostAsync($"{V1}groups", body, ct) ?? throw new DirectoryWriteException("GraphError");
        return new DirectoryGroup(Str(doc.RootElement, "id") ?? throw new DirectoryWriteException("GraphError"), displayName, true);
    }

    public async Task AddMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["@odata.id"] = $"{V1}directoryObjects/{Guid(userObjectId)}" });
        try
        {
            using var _ = await WhenReplicatedAsync(() => PostAsync($"{V1}groups/{Guid(groupObjectId)}/members/$ref", body, ct), ct);
        }
        catch (DirectoryWriteException e) when (e.Category == "AlreadyExists")
        {
            // Already a member: done.
        }
    }

    /// <summary>A security group mail nickname: ASCII letters and digits only, at most 64 characters.</summary>
    internal static string Nickname(string displayName)
    {
        var s = new string(displayName.Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 0 ? "pmogroup" : s[..Math.Min(64, s.Length)];
    }

    /// <summary>
    /// A user or group created a moment ago may not be readable by the next call yet (F58): wait briefly and try again
    /// (twice) in this run, so the password and the pass can still go out together.
    /// </summary>
    private async Task<T> WhenReplicatedAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (DirectoryWriteException e) when (e.Category == "NotReplicated" && attempt < 2)
            {
                await Task.Delay(replicationDelay * (attempt + 1), ct);
            }
        }
    }

    /// <summary>The agent's own service principal object id, from the Graph token's <c>oid</c> claim.</summary>
    private async Task<string> OwnObjectIdAsync(CancellationToken ct)
    {
        using var claims = await ClaimsAsync(ct);
        return Guid(Str(claims.RootElement, "oid") ?? throw new DirectoryWriteException("IdentityUnavailable"));
    }

    /// <summary>The Graph token's claims (application permissions are its <c>roles</c>, F61). The token itself is never kept.</summary>
    private async Task<JsonDocument> ClaimsAsync(CancellationToken ct)
    {
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([GraphDirectoryReader.Scope]), ct);
        }
        catch (Azure.Identity.AuthenticationFailedException e)
        {
            throw new DirectoryWriteException("IdentityUnavailable", e);
        }
        var parts = token.Token.Split('.');
        if (parts.Length < 2) throw new DirectoryWriteException("IdentityUnavailable");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        try
        {
            return JsonDocument.Parse(Convert.FromBase64String(payload));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new DirectoryWriteException("IdentityUnavailable", e);
        }
    }

    private const string TapSuffix = "/authentication/temporaryAccessPassMethods";

    private static bool Allowed(string url) =>
        url == $"{V1}users" || url == $"{V1}groups"
        || (url.StartsWith($"{V1}groups/", StringComparison.Ordinal) && url.EndsWith("/members/$ref", StringComparison.Ordinal)
            && System.Guid.TryParse(url[$"{V1}groups/".Length..^"/members/$ref".Length], out _))
        || (url.StartsWith($"{V1}users/", StringComparison.Ordinal) && url.EndsWith(TapSuffix, StringComparison.Ordinal)
            && System.Guid.TryParse(url[$"{V1}users/".Length..^TapSuffix.Length], out _));

    /// <summary>POST to the four allowed create URLs only. 429 and 5xx are retried once; errors become categories.</summary>
    private async Task<JsonDocument?> PostAsync(string url, string body, CancellationToken ct)
    {
        if (!Allowed(url)) throw new DirectoryWriteException("WriteRefused");
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([GraphDirectoryReader.Scope]), ct);
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new DirectoryWriteException("GraphUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new DirectoryWriteException("IdentityUnavailable", e);
            }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                // Graph error text is matched only to classify; it is never logged or stored.
                throw new DirectoryWriteException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "GraphForbidden",
                    HttpStatusCode.BadRequest when text.Contains("already exist", StringComparison.OrdinalIgnoreCase) => "AlreadyExists",
                    // Just-created group or user not replicated yet (F58): retried briefly in this run, then on the next agent run.
                    HttpStatusCode.NotFound => "NotReplicated",
                    HttpStatusCode.BadRequest when text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("don't exist", StringComparison.OrdinalIgnoreCase) => "NotReplicated",
                    _ when retryable => "GraphUnavailable",
                    _ => "GraphError",
                });
            }
        }
    }

    private static string Guid(string id) =>
        System.Guid.TryParse(id, out var g) ? g.ToString("D") : throw new DirectoryWriteException("GraphError");

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var t) ? t : null;
}
