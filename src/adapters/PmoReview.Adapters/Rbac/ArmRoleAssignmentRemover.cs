using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Rbac;

/// <summary>
/// Azure RBAC, F5 offboarding (v1.1; worker only, created only with <c>AGENT_OFFBOARDING</c>): deletes one role assignment
/// (api-version 2022-04-01, F83) — after reading it and checking that its principal is the user being offboarded and of
/// type User. The worker identity holds Role Based Access Control Administrator with a users-only delete condition per
/// subscription in scope, so Azure refuses anything else too. A 404 means already gone: done.
/// </summary>
public sealed class ArmRoleAssignmentRemover(HttpClient http, TokenCredential credential) : IRoleAssignmentRemover
{
    private const string Arm = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";

    public async Task DeleteAsync(RoleAssignmentRef assignment, string principalObjectId, CancellationToken ct)
    {
        if (!IsAssignmentId(assignment.Id) || !Guid.TryParse(principalObjectId, out var principal)) throw new DirectoryWriteException("WriteRefused");
        var url = $"{Arm}{assignment.Id}?api-version={ApiVersion}";
        using (var current = await SendAsync(HttpMethod.Get, url, ct))
        {
            if (current is null) return; // already gone
            var p = current.RootElement.TryGetProperty("properties", out var props) ? props : default;
            if (p.ValueKind != JsonValueKind.Object
                || !Guid.TryParse(p.TryGetProperty("principalId", out var pid) ? pid.GetString() : null, out var actual) || actual != principal
                || !string.Equals(p.TryGetProperty("principalType", out var pt) ? pt.GetString() : null, "User", StringComparison.Ordinal))
                throw new DirectoryWriteException("WriteRefused"); // never another principal, never a group or an app
        }
        using var _ = await SendAsync(HttpMethod.Delete, url, ct);
    }

    /// <summary>"/subscriptions/{…}/…/providers/Microsoft.Authorization/roleAssignments/{guid}" (or under a management group).</summary>
    public static bool IsAssignmentId(string id)
    {
        const string marker = "/providers/Microsoft.Authorization/roleAssignments/";
        var at = id.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at > 0 && ArmRoleAssignmentReader.IsAzureScope(id) && Guid.TryParse(id[(at + marker.Length)..], out _) && !id.Contains('?') && !id.Contains("..", StringComparison.Ordinal);
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([ArmRoleAssignmentReader.Scope]), ct);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new DirectoryWriteException("ArmUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new DirectoryWriteException("IdentityUnavailable", e);
            }
            using (res)
            {
                if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
                if (res.IsSuccessStatusCode)
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
                }
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                throw new DirectoryWriteException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "ArmForbidden",
                    _ when retryable => "ArmUnavailable",
                    _ => "ArmError",
                });
            }
        }
    }
}
