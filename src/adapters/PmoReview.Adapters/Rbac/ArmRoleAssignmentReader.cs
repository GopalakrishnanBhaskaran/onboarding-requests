using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;

namespace PmoReview.Adapters.Rbac;

/// <summary>
/// Azure RBAC role assignments, read only (Stage 5 and 6 verification; v1.1 F5 offboarding plan and verify). The worker
/// identity needs Reader (or "Microsoft.Authorization/roleAssignments/read") on the scopes it reads. Only Azure scopes are
/// readable here; any other scope type (e.g. a Databricks workspace role) returns null and the agent verifies against the
/// response document instead.
/// </summary>
public sealed class ArmRoleAssignmentReader(HttpClient http, TokenCredential credential) : IRoleAssignmentReader, IRoleAssignmentLister
{
    private const string SubscriptionsApiVersion = "2022-12-01";

    /// <summary>F84: the subscriptions the identity can see.</summary>
    public async Task<IReadOnlyList<string>> SubscriptionsAsync(CancellationToken ct)
    {
        using var doc = await GetAsync($"{Arm}/subscriptions?api-version={SubscriptionsApiVersion}", ct);
        return Values(doc).Select(s => Str(s, "subscriptionId")).Where(s => Guid.TryParse(s, out _)).Select(s => s!).ToList();
    }

    /// <summary>F5 (F82): the principal's assignments at, above or below the subscription; null when the identity may not read there.</summary>
    public async Task<IReadOnlyList<RoleAssignmentRef>?> AssignmentsInSubscriptionAsync(string subscriptionId, string principalObjectId, CancellationToken ct)
    {
        if (!Guid.TryParse(subscriptionId, out var sub) || !Guid.TryParse(principalObjectId, out var principal)) return null;
        var filter = Uri.EscapeDataString($"principalId eq '{principal:D}'");
        JsonDocument doc;
        try
        {
            doc = await GetAsync($"{Arm}/subscriptions/{sub:D}/providers/Microsoft.Authorization/roleAssignments?api-version={ApiVersion}&$filter={filter}", ct);
        }
        catch (DirectoryReadException e) when (e.Category is "ArmForbidden" or "ArmScopeNotFound")
        {
            return null;
        }
        using (doc)
        {
            return Values(doc).Select(a => (Id: Str(a, "id"), P: a.TryGetProperty("properties", out var p) ? p : default))
                .Where(a => a.Id is not null && a.P.ValueKind == JsonValueKind.Object)
                .Select(a => new RoleAssignmentRef(a.Id!, Str(a.P, "scope") ?? "", (Str(a.P, "roleDefinitionId") ?? "").Split('/')[^1],
                    Str(a.P, "principalId") ?? "", Str(a.P, "principalType") ?? ""))
                .ToList();
        }
    }

    public const string Scope = "https://management.azure.com/.default";
    private const string Arm = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";

    public async Task<IReadOnlyList<AssignedRole>?> ListForPrincipalAsync(string principalObjectId, string? scope, CancellationToken ct)
    {
        if (!IsAzureScope(scope) || !Guid.TryParse(principalObjectId, out var principal)) return null;
        var normalized = scope!.TrimEnd('/');
        var filter = Uri.EscapeDataString($"principalId eq '{principal:D}'");
        using var doc = await GetAsync($"{Arm}{normalized}/providers/Microsoft.Authorization/roleAssignments?api-version={ApiVersion}&$filter={filter}", ct);
        var result = new List<AssignedRole>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Values(doc))
        {
            if (!a.TryGetProperty("properties", out var p)) continue;
            var at = Str(p, "scope")?.TrimEnd('/');
            // Exactly this scope: inherited assignments from above and assignments below are not this request's role.
            if (!string.Equals(at, normalized, StringComparison.OrdinalIgnoreCase)) continue;
            var definition = Str(p, "roleDefinitionId");
            if (definition is null || !definition.StartsWith('/')) continue;
            if (!names.TryGetValue(definition, out var name))
            {
                using var d = await GetAsync($"{Arm}{definition}?api-version={ApiVersion}", ct);
                name = d.RootElement.TryGetProperty("properties", out var dp) ? Str(dp, "roleName") ?? definition : definition;
                names[definition] = name;
            }
            result.Add(new AssignedRole(name, normalized));
        }
        return result;
    }

    public static bool IsAzureScope(string? scope) =>
        scope is not null && (scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                              || scope.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase));

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new DirectoryReadException("ArmUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new DirectoryReadException("IdentityUnavailable", e);
            }
            using (res)
            {
                if (res.IsSuccessStatusCode)
                    return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                throw new DirectoryReadException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "ArmForbidden",
                    HttpStatusCode.NotFound => "ArmScopeNotFound",
                    _ when retryable => "ArmUnavailable",
                    _ => "ArmError",
                });
            }
        }
    }

    private static IEnumerable<JsonElement> Values(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
