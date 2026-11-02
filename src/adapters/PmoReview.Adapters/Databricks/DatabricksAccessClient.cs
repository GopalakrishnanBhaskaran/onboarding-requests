using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using PmoReview.Core.Databricks;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Databricks;

/// <summary>
/// Databricks REST for a request's group (v1.2, worker only, held only with <c>AGENT_DATABRICKS</c>; the worker's managed
/// identity is a Databricks service principal, F118). Calls, all read before written, all add-only:
/// <list type="bullet">
/// <item>A3 account: <c>GET /api/2.0/identity/accounts/{account}/external-groups/{entra object id}</c> adds the Entra group (F111).</item>
/// <item>A3 workspace: <c>GET|PUT /api/2.0/accounts/{account}/workspaces/{id}/permissionassignments[/principals/{id}]</c>, USER only (F112).</item>
/// <item>A4: workspace SCIM <c>GET /api/2.0/preview/scim/v2/Groups?filter=displayName eq "…"</c>, <c>PATCH …/Groups/{id}</c> add entitlements (F114).</item>
/// <item>A5: <c>GET /api/2.1/unity-catalog/effective-permissions/{type}/{name}?principal=</c>, <c>PATCH /api/2.1/unity-catalog/permissions/{type}/{name}</c> add (F117).</item>
/// </list>
/// Never a remove, never ADMIN, never a name that is not a Unity Catalog name. Errors are categories only.
/// </summary>
public sealed class DatabricksAccessClient(HttpClient http, TokenCredential credential, string accountId) : IDatabricksAccess
{
    /// <summary>Azure Databricks resource id for Entra tokens (F113).</summary>
    public const string Scope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";
    private const string Accounts = "https://accounts.azuredatabricks.net";
    private static readonly Regex UcName = new("^[A-Za-z0-9_]{1,255}(\\.[A-Za-z0-9_]{1,255})?$", RegexOptions.CultureInvariant);
    private static readonly Regex GroupName = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,255}$", RegexOptions.CultureInvariant);

    private readonly string account = Guid.TryParse(accountId, out var a) ? a.ToString("D") : throw new ArgumentException("DATABRICKS_ACCOUNT_ID must be a GUID.");

    public async Task<long> AddGroupToAccountAsync(string entraGroupObjectId, CancellationToken ct)
    {
        if (!Guid.TryParse(entraGroupObjectId, out var oid)) throw new DirectoryWriteException("WriteRefused");
        var json = await SendAsync(HttpMethod.Get, $"{Accounts}/api/2.0/identity/accounts/{account}/external-groups/{oid:D}", null, write: true, ct);
        return json is { } j && j.TryGetProperty("internal_id", out var id) && Long(id) is { } v ? v : throw new DirectoryWriteException("DatabricksError");
    }

    public async Task<IReadOnlyList<string>> WorkspacePermissionsAsync(DatabricksWorkspace ws, string groupName, CancellationToken ct)
    {
        Group(groupName);
        var json = await SendAsync(HttpMethod.Get, $"{Accounts}/api/2.0/accounts/{account}/workspaces/{ws.Id}/permissionassignments", null, write: false, ct);
        if (json is not { } j || !j.TryGetProperty("permission_assignments", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        foreach (var a in list.EnumerateArray())
        {
            if (!a.TryGetProperty("principal", out var p) || !p.TryGetProperty("group_name", out var n)
                || !string.Equals(n.GetString(), groupName, StringComparison.OrdinalIgnoreCase)) continue;
            return a.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array
                ? perms.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
        }
        return [];
    }

    public async Task AssignUserAsync(DatabricksWorkspace ws, long principalId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Put, $"{Accounts}/api/2.0/accounts/{account}/workspaces/{ws.Id}/permissionassignments/principals/{principalId}",
            new { permissions = new[] { "USER" } }, write: true, ct);

    public async Task<IReadOnlyList<string>?> EntitlementsAsync(DatabricksWorkspace ws, string groupName, CancellationToken ct)
    {
        var group = await WorkspaceGroupAsync(ws, groupName, write: false, ct);
        if (group is not { } g) return null;
        return g.TryGetProperty("entitlements", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "").Where(x => x.Length > 0).ToList()
            : [];
    }

    public async Task AddEntitlementsAsync(DatabricksWorkspace ws, string groupName, IReadOnlyList<string> entitlements, CancellationToken ct)
    {
        if (entitlements.Count == 0) return;
        if (entitlements.Any(e => !DatabricksPolicy.AllowedEntitlements.Contains(e))) throw new DirectoryWriteException("WriteRefused");
        var group = await WorkspaceGroupAsync(ws, groupName, write: true, ct) ?? throw new DirectoryWriteException("DatabricksNotFound");
        var id = group.GetProperty("id").GetString() ?? throw new DirectoryWriteException("DatabricksError");
        if (!Regex.IsMatch(id, "^[0-9]{1,30}$")) throw new DirectoryWriteException("DatabricksError");
        await SendAsync(HttpMethod.Patch, $"{ws.Url}api/2.0/preview/scim/v2/Groups/{id}", new Dictionary<string, object>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
            ["Operations"] = new[] { new { op = "add", path = "entitlements", value = entitlements.Select(e => new { value = e }).ToArray() } },
        }, write: true, ct, scim: true);
    }

    public async Task<IReadOnlyList<string>> EffectivePrivilegesAsync(DatabricksWorkspace ws, string securableType, string fullName, string groupName, CancellationToken ct)
    {
        var url = $"{ws.Url}api/2.1/unity-catalog/effective-permissions/{Securable(securableType)}/{Uc(fullName)}?principal={Uri.EscapeDataString(Group(groupName))}";
        var json = await SendAsync(HttpMethod.Get, url, null, write: false, ct);
        if (json is not { } j || !j.TryGetProperty("privilege_assignments", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(a => a.TryGetProperty("principal", out var p) && string.Equals(p.GetString(), groupName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a.TryGetProperty("privileges", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : p.TryGetProperty("privilege", out var v) ? v.GetString() : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task GrantAsync(DatabricksWorkspace ws, string securableType, string fullName, string groupName, IReadOnlyList<string> privileges, CancellationToken ct)
    {
        if (privileges.Count == 0) return;
        if (privileges.Any(p => !DatabricksPolicy.AllowedPrivileges.Contains(p))) throw new DirectoryWriteException("WriteRefused");
        await SendAsync(HttpMethod.Patch, $"{ws.Url}api/2.1/unity-catalog/permissions/{Securable(securableType)}/{Uc(fullName)}",
            new { changes = new[] { new { principal = Group(groupName), add = privileges.ToArray() } } }, write: true, ct);
    }

    private async Task<JsonElement?> WorkspaceGroupAsync(DatabricksWorkspace ws, string groupName, bool write, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq \"{Group(groupName)}\"");
        var json = await SendAsync(HttpMethod.Get, $"{ws.Url}api/2.0/preview/scim/v2/Groups?filter={filter}", null, write, ct, scim: true);
        if (json is not { } j || !j.TryGetProperty("Resources", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var g in list.EnumerateArray())
            if (g.TryGetProperty("displayName", out var n) && string.Equals(n.GetString(), groupName, StringComparison.OrdinalIgnoreCase)) return g.Clone();
        return null;
    }

    private static string Securable(string type) => type is "catalog" or "schema" ? type : throw new DirectoryWriteException("WriteRefused");
    private static string Uc(string name) => UcName.IsMatch(name) ? name : throw new DirectoryWriteException("WriteRefused");
    private static string Group(string name) => GroupName.IsMatch(name) ? name : throw new DirectoryWriteException("WriteRefused");

    private static long? Long(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(e.GetString(), out var s) => s,
        _ => null,
    };

    private async Task<JsonElement?> SendAsync(HttpMethod method, string url, object? body, bool write, CancellationToken ct, bool scim = false)
    {
        DirectoryReadException Fail(string category, Exception? inner = null) =>
            write ? new DirectoryWriteException(category, inner) : new DirectoryReadException(category, inner);
        var text = body is null ? null : JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
                using var req = new HttpRequestMessage(method, url);
                if (text is not null) req.Content = new StringContent(text, Encoding.UTF8, scim ? "application/scim+json" : "application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw Fail("DatabricksUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw Fail("IdentityUnavailable", e);
            }

            using (res)
            {
                var content = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content).RootElement.Clone();
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                throw Fail(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "DatabricksForbidden",
                    HttpStatusCode.NotFound => "DatabricksNotFound",
                    _ when retryable => "DatabricksUnavailable",
                    _ => "DatabricksError",
                });
            }
        }
    }
}
