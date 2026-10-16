using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Handoff;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// The app's only Entra write (auto-assign mode): POST one "adminAdd" accessPackageAssignmentRequest
/// (Graph v1.0) for the joiner. Before writing it checks, with GETs, that the access package belongs to the
/// allowed catalog and has the app's assignment policy, and resolves the joiner's object id.
/// No other request type, URL or method is ever sent. Needs EntitlementManagement.ReadWrite.All and User.ReadBasic.All.
/// </summary>
public sealed class GraphEntraAssignmentWriter : IEntraAssignmentWriter
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";
    private const string Em = Graph + "identityGovernance/entitlementManagement/";
    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly string _allowedCatalogId;
    private readonly string _policyName;

    public GraphEntraAssignmentWriter(HttpClient http, TokenCredential credential, string allowedCatalogId, string policyName)
    {
        if (!Guid.TryParse(allowedCatalogId, out var catalog))
            throw new ArgumentException("ENTRA_ALLOWED_CATALOG_ID must be the catalog's GUID.", nameof(allowedCatalogId));
        if (string.IsNullOrWhiteSpace(policyName))
            throw new ArgumentException("ENTRA_AUTO_ASSIGN_POLICY is required.", nameof(policyName));
        (_http, _credential, _allowedCatalogId, _policyName) = (http, credential, catalog.ToString("D"), policyName.Trim());
    }

    public async Task<EntraRequest> RequestAssignmentAsync(AssignmentOrder order, CancellationToken ct)
    {
        if (!Guid.TryParse(order.AccessPackageId, out var pkgGuid)) throw new EntraWriteException("InvalidAccessPackageId");
        var pkg = pkgGuid.ToString("D");

        // The app's policy on this package, with its catalog: one GET answers "allowed catalog?" and "which policy?".
        string policyId;
        var policyUrl = $"{Em}assignmentPolicies?$expand=accessPackage,catalog&$filter={Uri.EscapeDataString($"accessPackage/id eq '{pkg}'")}";
        using (var policies = await SendAsync(HttpMethod.Get, policyUrl, null, "AccessPackageNotFound", ct))
        {
            var mine = policies.RootElement.TryGetProperty("value", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Where(p => string.Equals(Str(p, "displayName"), _policyName, StringComparison.Ordinal)).ToList()
                : [];
            if (mine.Count == 0) throw new EntraWriteException("PolicyNotFound");
            var policy = mine[0];
            var catalogId = policy.TryGetProperty("catalog", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "id") : null;
            var packageId = policy.TryGetProperty("accessPackage", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "id") : null;
            if (!string.Equals(catalogId, _allowedCatalogId, StringComparison.OrdinalIgnoreCase)) throw new EntraWriteException("CatalogNotAllowed");
            if (!string.Equals(packageId, pkg, StringComparison.OrdinalIgnoreCase)) throw new EntraWriteException("PolicyNotFound");
            policyId = Str(policy, "id") ?? throw new EntraWriteException("PolicyNotFound");
        }

        string joinerId;
        using (var user = await SendAsync(HttpMethod.Get, $"{Graph}users/{Uri.EscapeDataString(order.JoinerUpn)}?$select=id", null, "JoinerNotFound", ct))
            joinerId = Str(user.RootElement, "id") ?? throw new EntraWriteException("JoinerNotFound");

        var body = JsonSerializer.Serialize(new
        {
            requestType = "adminAdd",
            justification = order.Justification,
            assignment = new { targetId = joinerId, assignmentPolicyId = policyId, accessPackageId = pkg },
            schedule = new
            {
                startDateTime = order.Start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                expiration = new { type = "afterDateTime", endDateTime = order.End.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
            },
        });
        using var created = await SendAsync(HttpMethod.Post, $"{Em}assignmentRequests", body, "AssignmentRejected", ct);
        var r = created.RootElement;
        return new EntraRequest(
            Str(r, "id") ?? throw new EntraWriteException("GraphError"),
            Str(r, "requestType") ?? "adminAdd",
            Str(r, "state") ?? "submitted",
            Str(r, "status"),
            DateTimeOffset.TryParse(Str(r, "createdDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow,
            null, order.Start, order.End);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? body, string notFoundOrBadRequest, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await _credential.GetTokenAsync(new TokenRequestContext([GraphEntraGovernanceReader.Scope]), ct);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                res = await _http.SendAsync(req, ct);
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
                    return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                // 429 means Graph did not process the call, so one retry is safe even for the POST. A 5xx on the POST is
                // not retried here (it may have been processed); the next run sees the request in Entra instead.
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

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
