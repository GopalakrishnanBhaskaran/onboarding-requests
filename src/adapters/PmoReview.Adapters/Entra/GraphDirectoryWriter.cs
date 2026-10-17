using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Microsoft Graph v1.0, agent provisioning (v2.1, worker only, after both approvals). Exactly three create calls:
/// <c>POST /users</c> (User.Create), <c>POST /groups</c> (Group.Create, owned by the agent's own identity) and
/// <c>POST /groups/{id}/members/$ref</c> (GroupMember.ReadWrite.All) — VERIFIED_FACTS F56–F58. No update, delete,
/// licence or authentication-method call exists here; any other URL is refused before it is sent.
/// </summary>
public sealed class GraphDirectoryWriter(HttpClient http, TokenCredential credential) : IDirectoryWriter
{
    private const string V1 = "https://graph.microsoft.com/v1.0/";
    private const string PasswordChars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#$%&*+-=?@^_";

    public async Task<DirectoryUser> CreateUserAsync(NewUser user, CancellationToken ct)
    {
        // Entra requires an initial password. It is random, sent once and never kept or logged; the user signs in
        // with the ID team's one-time TAP and registers a permanent method.
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["accountEnabled"] = true,
            ["displayName"] = user.DisplayName,
            ["givenName"] = user.GivenName,
            ["surname"] = user.Surname,
            ["mailNickname"] = user.MailNickname,
            ["userPrincipalName"] = user.UserPrincipalName,
            ["passwordProfile"] = new Dictionary<string, object> { ["forceChangePasswordNextSignIn"] = true, ["password"] = RandomPassword() },
        });
        using var doc = await PostAsync($"{V1}users", body, ct) ?? throw new DirectoryWriteException("GraphError");
        return new DirectoryUser(Str(doc.RootElement, "id") ?? throw new DirectoryWriteException("GraphError"), user.UserPrincipalName, "Member", true);
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
            using var _ = await PostAsync($"{V1}groups/{Guid(groupObjectId)}/members/$ref", body, ct);
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

    internal static string RandomPassword()
    {
        while (true)
        {
            var p = RandomNumberGenerator.GetString(PasswordChars, 32);
            if (p.Any(char.IsAsciiLetterUpper) && p.Any(char.IsAsciiLetterLower) && p.Any(char.IsAsciiDigit) && p.Any(c => !char.IsAsciiLetterOrDigit(c)))
                return p;
        }
    }

    /// <summary>The agent's own service principal object id, from the Graph token's <c>oid</c> claim.</summary>
    private async Task<string> OwnObjectIdAsync(CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([GraphDirectoryReader.Scope]), ct);
        var parts = token.Token.Split('.');
        if (parts.Length < 2) throw new DirectoryWriteException("IdentityUnavailable");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
        return Guid(Str(claims.RootElement, "oid") ?? throw new DirectoryWriteException("IdentityUnavailable"));
    }

    private static bool Allowed(string url) =>
        url == $"{V1}users" || url == $"{V1}groups"
        || (url.StartsWith($"{V1}groups/", StringComparison.Ordinal) && url.EndsWith("/members/$ref", StringComparison.Ordinal)
            && System.Guid.TryParse(url[$"{V1}groups/".Length..^"/members/$ref".Length], out _));

    /// <summary>POST to the three allowed create URLs only. 429 and 5xx are retried once; errors become categories.</summary>
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
                    // Just-created group or user not replicated yet (F58): retried on the next agent run.
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound when text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
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
}
