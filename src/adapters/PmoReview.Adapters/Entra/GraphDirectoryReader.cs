using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Microsoft Graph v1.0, read only. The worker identity holds read permissions only (User.Read.All,
/// GroupMember.Read.All, UserAuthenticationMethod.Read.All, AuditLog.Read.All). The only non-GET call is the
/// documented read action <c>checkMemberGroups</c>; there is no code path that writes to Entra.
/// </summary>
public sealed class GraphDirectoryReader(HttpClient http, TokenCredential credential) : IDirectoryReader
{
    public const string Scope = "https://graph.microsoft.com/.default";
    private const string V1 = "https://graph.microsoft.com/v1.0/";
    private const int MaxPages = 20;

    public async Task<DirectoryUser?> FindUserAsync(string userPrincipalName, CancellationToken ct)
    {
        using var doc = await GetAsync($"{V1}users?$filter={Q($"userPrincipalName eq '{Literal(userPrincipalName)}'")}&$select=id,userPrincipalName,userType,accountEnabled", ct);
        var u = Values(doc).FirstOrDefault();
        return u.ValueKind == JsonValueKind.Undefined ? null
            : new DirectoryUser(Str(u, "id") ?? "", Str(u, "userPrincipalName") ?? userPrincipalName, Str(u, "userType"), Bool(u, "accountEnabled") ?? true);
    }

    public async Task<IReadOnlyList<DirectoryGroup>> FindGroupsAsync(string displayName, CancellationToken ct)
    {
        var items = await GetAllAsync($"{V1}groups?$filter={Q($"displayName eq '{Literal(displayName)}'")}&$select=id,displayName,securityEnabled", ct);
        return items.Select(g => new DirectoryGroup(Str(g, "id") ?? "", Str(g, "displayName") ?? displayName, Bool(g, "securityEnabled") ?? false)).ToList();
    }

    public async Task<bool> IsMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct)
    {
        // Read action (POST by design in Graph): returns the subset of the given groups the user is a member of.
        var body = JsonSerializer.Serialize(new { groupIds = new[] { Guid(groupObjectId) } });
        using var doc = await SendAsync(HttpMethod.Post, $"{V1}users/{Guid(userObjectId)}/checkMemberGroups", body, ct)
                        ?? throw new DirectoryReadException("GraphError");
        return Values(doc).Any(v => v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), Guid(groupObjectId), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AuthMethods> GetAuthMethodsAsync(string userObjectId, CancellationToken ct)
    {
        var items = await GetAllAsync($"{V1}users/{Guid(userObjectId)}/authentication/methods", ct);
        var types = new List<string>();
        TapMethod? tap = null;
        foreach (var m in items)
        {
            var type = (Str(m, "@odata.type") ?? "").Replace("#microsoft.graph.", "", StringComparison.Ordinal).Replace("AuthenticationMethod", "", StringComparison.Ordinal);
            if (type.Length == 0) continue;
            types.Add(type);
            if (string.Equals(type, "temporaryAccessPass", StringComparison.OrdinalIgnoreCase))
            {
                // Entra returns the pass itself only on creation; a read carries metadata only.
                tap = new TapMethod(Date(m, "createdDateTime"), Date(m, "startDateTime"),
                    m.TryGetProperty("lifetimeInMinutes", out var l) && l.TryGetInt32(out var minutes) ? minutes : null,
                    Bool(m, "isUsableOnce"), Bool(m, "isUsable"), Str(m, "methodUsabilityReason"));
            }
        }
        return new AuthMethods(types, tap);
    }

    public async Task<SignInSummary?> LastSuccessfulSignInAsync(string userObjectId, DateTimeOffset since, CancellationToken ct)
    {
        var filter = $"userId eq '{Guid(userObjectId)}' and createdDateTime ge {since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} and status/errorCode eq 0";
        using var doc = await GetAsync($"{V1}auditLogs/signIns?$filter={Q(filter)}&$top=1", ct);
        var s = Values(doc).FirstOrDefault();
        return s.ValueKind == JsonValueKind.Undefined ? null : new SignInSummary(Date(s, "createdDateTime") ?? since, true);
    }

    public async Task<string?> GetManagerAsync(string userPrincipalName, CancellationToken ct)
    {
        var user = await FindUserAsync(userPrincipalName, ct);
        if (user is null) return null;
        using var doc = await SendAsync(HttpMethod.Get, $"{V1}users/{Guid(user.ObjectId)}/manager?$select=userPrincipalName,mail", null, ct, notFoundIsNull: true);
        return doc is null ? null : Str(doc.RootElement, "mail") ?? Str(doc.RootElement, "userPrincipalName");
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, url, null, ct) ?? throw new DirectoryReadException("GraphError");

    private async Task<List<JsonElement>> GetAllAsync(string url, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        string? next = url;
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var doc = await GetAsync(next, ct);
            all.AddRange(Values(doc).Select(v => v.Clone()));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
            if (next is not null && !next.StartsWith(V1, StringComparison.Ordinal)) next = null; // only ever follow links back to Graph
        }
        return all;
    }

    /// <summary>GET, or POST to checkMemberGroups only. 429 and 5xx are retried once; errors become categories.</summary>
    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct, bool notFoundIsNull = false)
    {
        if (!url.StartsWith(V1, StringComparison.Ordinal)) throw new DirectoryReadException("GraphError");
        if (method != HttpMethod.Get && !(method == HttpMethod.Post && url.EndsWith("/checkMemberGroups", StringComparison.Ordinal)))
            throw new DirectoryReadException("WriteRefused");
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new DirectoryReadException("GraphUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new DirectoryReadException("IdentityUnavailable", e);
            }

            using (res)
            {
                if (res.IsSuccessStatusCode)
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
                }
                if (notFoundIsNull && res.StatusCode == HttpStatusCode.NotFound) return null;
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                throw new DirectoryReadException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "GraphForbidden",
                    HttpStatusCode.NotFound => "GraphNotFound",
                    _ when retryable => "GraphUnavailable",
                    _ => "GraphError",
                });
            }
        }
    }

    private static string Q(string filter) => Uri.EscapeDataString(filter);

    /// <summary>OData string literal inside a $filter (single quotes doubled).</summary>
    public static string Literal(string s) => s.Replace("'", "''");

    private static string Guid(string id) => System.Guid.TryParse(id, out var g) ? g.ToString("D") : throw new DirectoryReadException("InvalidObjectId");

    private static IEnumerable<JsonElement> Values(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
