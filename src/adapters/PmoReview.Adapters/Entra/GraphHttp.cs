using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Handoff;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Microsoft Graph v1.0 calls for the worker's Entra writes. Errors become <see cref="EntraWriteException"/> categories
/// (no response details). 429 is retried once for any method (Graph did not process it); 5xx only for GET, because a
/// write may have been applied: the next worker run re-reads instead.
/// </summary>
internal sealed class GraphHttp(HttpClient http, TokenCredential credential)
{
    public const string V1 = "https://graph.microsoft.com/v1.0/";
    public const string Em = V1 + "identityGovernance/entitlementManagement/";

    public async Task<JsonDocument?> SendAsync(HttpMethod method, string url, object? body, string notFoundOrBadRequest, CancellationToken ct)
    {
        if (!url.StartsWith(V1, StringComparison.Ordinal)) throw new EntraWriteException("GraphError"); // Graph only
        var json = body is null ? null : JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([GraphEntraGovernanceReader.Scope]), ct);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new EntraWriteException("GraphUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new EntraWriteException("IdentityUnavailable", e);
            }

            using (res)
            {
                if (res.IsSuccessStatusCode)
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
                }
                var retry = attempt == 0 && (res.StatusCode == HttpStatusCode.TooManyRequests
                                             || (method == HttpMethod.Get && res.StatusCode >= HttpStatusCode.InternalServerError));
                if (retry)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                throw new EntraWriteException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "GraphForbidden",
                    HttpStatusCode.NotFound or HttpStatusCode.BadRequest => notFoundOrBadRequest,
                    HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => "GraphUnavailable",
                    _ => "GraphError",
                });
            }
        }
    }

    public async Task<JsonDocument> GetAsync(string url, string notFound, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, url, null, notFound, ct) ?? throw new EntraWriteException("GraphError");

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Values(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>Names typed in the portal: ignore case, surrounding and repeated spaces.</summary>
    public static bool SameName(string? a, string b) =>
        a is not null && string.Equals(string.Join(' ', a.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                                       string.Join(' ', b.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    /// <summary>OData string literal inside a $filter (single quotes doubled), URL-encoded by the caller.</summary>
    public static string Literal(string s) => s.Replace("'", "''");
}
