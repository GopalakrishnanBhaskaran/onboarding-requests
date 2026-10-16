using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Handoff;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Microsoft Graph v1.0, GET only. The identity needs the application permission
/// EntitlementManagement.Read.All and nothing else; this class has no code path that writes to Entra.
/// </summary>
public sealed class GraphEntraGovernanceReader(HttpClient http, TokenCredential credential) : IEntraGovernanceReader
{
    public const string Scope = "https://graph.microsoft.com/.default";
    private const string Base = "https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement/";
    private const int MaxPages = 50;

    public async Task<IReadOnlyList<EntraRequest>> ListRequestsAsync(string accessPackageId, CancellationToken ct)
    {
        var url = $"{Base}assignmentRequests?$expand=requestor,accessPackage&$filter={Uri.EscapeDataString($"accessPackage/id eq '{Guid(accessPackageId)}'")}";
        var items = await GetAllAsync(url, ct);
        return items.Select(e => new EntraRequest(
            Str(e, "id") ?? "",
            Str(e, "requestType") ?? "",
            Str(e, "state") ?? "",
            Str(e, "status"),
            Date(e, "createdDateTime") ?? DateTimeOffset.MinValue,
            Subject(e, "requestor"),
            ScheduleStart(e),
            ScheduleEnd(e))).ToList();
    }

    public async Task<IReadOnlyList<EntraAssignment>> ListAssignmentsAsync(string accessPackageId, CancellationToken ct)
    {
        var url = $"{Base}assignments?$expand=accessPackage,target&$filter={Uri.EscapeDataString($"accessPackage/id eq '{Guid(accessPackageId)}'")}";
        var items = await GetAllAsync(url, ct);
        return items.Select(e => new EntraAssignment(
            Str(e, "id") ?? "",
            Str(e, "state") ?? "",
            Str(e, "status"),
            Subject(e, "target"),
            ScheduleStart(e),
            ScheduleEnd(e),
            e.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("expiration", out var x) && x.ValueKind == JsonValueKind.Object ? Str(x, "type") : null,
            Date(e, "expiredDateTime"))).ToList();
    }

    private async Task<List<JsonElement>> GetAllAsync(string url, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        string? next = url;
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var doc = await GetJsonAsync(next, ct);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                all.AddRange(value.EnumerateArray().Select(v => v.Clone()));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
            // Only ever follow links back to Graph.
            if (next is not null && !next.StartsWith("https://graph.microsoft.com/", StringComparison.Ordinal)) next = null;
        }
        return all;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new EntraReadException("GraphUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new EntraReadException("IdentityUnavailable", e);
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
                throw new EntraReadException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "GraphForbidden",
                    HttpStatusCode.NotFound => "AccessPackageNotFound",
                    _ when retryable => "GraphUnavailable",
                    _ => "GraphError",
                });
            }
        }
    }

    private static string Guid(string id) =>
        System.Guid.TryParse(id, out var g) ? g.ToString("D") : throw new EntraReadException("InvalidAccessPackageId");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static EntraSubject? Subject(JsonElement e, string name) =>
        e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Object
            ? new EntraSubject(Str(s, "objectId"), Str(s, "principalName"), Str(s, "email"), Str(s, "displayName"))
            : null;

    private static DateTimeOffset? ScheduleStart(JsonElement e) =>
        e.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.Object ? Date(s, "startDateTime") : null;

    private static DateTimeOffset? ScheduleEnd(JsonElement e) =>
        e.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.Object
        && s.TryGetProperty("expiration", out var x) && x.ValueKind == JsonValueKind.Object ? Date(x, "endDateTime") : null;
}
