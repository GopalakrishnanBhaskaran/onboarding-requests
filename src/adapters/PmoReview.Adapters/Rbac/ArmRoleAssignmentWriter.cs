using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Rbac;

/// <summary>
/// Azure RBAC, agent role assignment (v2.1, worker only, after both approvals): one <c>PUT</c> of a role assignment
/// (api-version 2022-04-01, VERIFIED_FACTS F59) to a user or group, for role definitions on the allow-list only. The
/// assignment name is derived from scope + role + principal, so a repeat is the same assignment; an existing one counts
/// as done. No delete. The worker identity holds Role Based Access Control Administrator constrained by a condition to
/// the same roles and to user/group principals (F60), so Azure refuses anything else too.
/// </summary>
public sealed class ArmRoleAssignmentWriter(HttpClient http, TokenCredential credential, IReadOnlyCollection<string> allowedRoleIds) : IRoleAssignmentWriter
{
    private const string Arm = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";

    public async Task AssignAsync(string principalObjectId, string principalType, string roleDefinitionId, string scope, CancellationToken ct)
    {
        if (!System.Guid.TryParse(principalObjectId, out var principal) || !System.Guid.TryParse(roleDefinitionId, out var role)
            || principalType is not ("User" or "Group") || !ArmRoleAssignmentReader.IsAzureScope(scope)
            || !allowedRoleIds.Contains(role.ToString("D"), StringComparer.OrdinalIgnoreCase))
            throw new DirectoryWriteException("WriteRefused");
        var normalized = scope.TrimEnd('/');
        var name = AssignmentName(normalized, role, principal);
        var url = $"{Arm}{normalized}/providers/Microsoft.Authorization/roleAssignments/{name}?api-version={ApiVersion}";
        var body = JsonSerializer.Serialize(new
        {
            properties = new
            {
                roleDefinitionId = $"{normalized}/providers/Microsoft.Authorization/roleDefinitions/{role:D}",
                principalId = principal.ToString("D"),
                principalType, // set for a just-created principal (replication), F59
            },
        });

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([ArmRoleAssignmentReader.Scope]), ct);
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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
                if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Conflict) return; // 409 RoleAssignmentExists: done
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                var text = await res.Content.ReadAsStringAsync(ct);
                throw new DirectoryWriteException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "ArmForbidden",
                    // A just-created group not yet replicated to Azure RBAC: retried on the next agent run.
                    HttpStatusCode.BadRequest when text.Contains("PrincipalNotFound", StringComparison.OrdinalIgnoreCase) => "NotReplicated",
                    _ when retryable => "ArmUnavailable",
                    _ => "ArmError",
                });
            }
        }
    }

    /// <summary>Deterministic GUID from scope, role and principal (same inputs, same assignment).</summary>
    internal static string AssignmentName(string scope, Guid role, Guid principal)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope.ToLowerInvariant()}|{role:D}|{principal:D}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version-5 style bits
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }
}
