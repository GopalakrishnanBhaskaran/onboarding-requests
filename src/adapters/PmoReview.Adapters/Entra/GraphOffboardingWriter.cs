using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using PmoReview.Core.Directory;

namespace PmoReview.Adapters.Entra;

/// <summary>
/// Microsoft Graph v1.0, F5 offboarding writes (v1.1; Gokul's written approval 30 Oct 2026; worker only, created only with
/// <c>AGENT_OFFBOARDING</c>). Exactly five calls, VERIFIED_FACTS F71–F75: <c>POST /users/{id}/revokeSignInSessions</c>
/// (User.RevokeSessions.All), <c>DELETE /users/{id}/authentication/temporaryAccessPassMethods/{id}</c>
/// (UserAuthMethod-TAP.ReadWrite.All), <c>DELETE /groups/{id}/members/{id}/$ref</c> (always <c>/$ref</c>: without it the user
/// object itself would be deleted, F73; GroupMember.ReadWrite.All), <c>PATCH /users/{id}</c> with only
/// <c>accountEnabled=false</c> (User.EnableDisableAccount.All) and, only with <c>OFFBOARD_DELETE_USER</c>, <c>DELETE /users/{id}</c>
/// (soft delete, restorable 30 days; User.ReadWrite.All, not granted by default). Any other URL is refused before it is
/// sent. A 404 on a delete means already gone: done.
/// </summary>
public sealed class GraphOffboardingWriter(HttpClient http, TokenCredential credential, bool deleteUser = false) : IOffboardingWriter
{
    private const string V1 = "https://graph.microsoft.com/v1.0/";

    public Task RevokeSessionsAsync(string userObjectId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{V1}users/{Guid(userObjectId)}/revokeSignInSessions", null, ct);

    public Task DeleteTapAsync(string userObjectId, string methodId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{V1}users/{Guid(userObjectId)}/authentication/temporaryAccessPassMethods/{Guid(methodId)}", null, ct);

    public Task RemoveMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{V1}groups/{Guid(groupObjectId)}/members/{Guid(userObjectId)}/$ref", null, ct);

    public Task DisableAsync(string userObjectId, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{V1}users/{Guid(userObjectId)}", "{\"accountEnabled\":false}", ct);

    public Task DeleteUserAsync(string userObjectId, CancellationToken ct) =>
        deleteUser ? SendAsync(HttpMethod.Delete, $"{V1}users/{Guid(userObjectId)}", null, ct) : throw new DirectoryWriteException("WriteRefused");

    /// <summary>The only URLs this class may call, by method.</summary>
    public static bool Allowed(HttpMethod method, string url, string? body)
    {
        if (!url.StartsWith(V1, StringComparison.Ordinal)) return false;
        var parts = url[V1.Length..].Split('/');
        bool Id(int i) => i < parts.Length && System.Guid.TryParse(parts[i], out _);
        var m = method.Method;
        return (m == "POST" && parts.Length == 3 && parts[0] == "users" && Id(1) && parts[2] == "revokeSignInSessions" && body is null)
            || (m == "DELETE" && parts.Length == 5 && parts[0] == "users" && Id(1) && parts[2] == "authentication" && parts[3] == "temporaryAccessPassMethods" && Id(4))
            || (m == "DELETE" && parts.Length == 5 && parts[0] == "groups" && Id(1) && parts[2] == "members" && Id(3) && parts[4] == "$ref")
            || (m == "PATCH" && parts.Length == 2 && parts[0] == "users" && Id(1) && body == "{\"accountEnabled\":false}")
            || (m == "DELETE" && parts.Length == 2 && parts[0] == "users" && Id(1));
    }

    private async Task SendAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        if (!Allowed(method, url, body)) throw new DirectoryWriteException("WriteRefused");
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([GraphDirectoryReader.Scope]), ct);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
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
                if (res.IsSuccessStatusCode) return;
                if (res.StatusCode == HttpStatusCode.NotFound && method == HttpMethod.Delete) return; // already gone: done
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                throw new DirectoryWriteException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "GraphForbidden",
                    HttpStatusCode.NotFound => "GraphNotFound",
                    _ when retryable => "GraphUnavailable",
                    _ => "GraphError",
                });
            }
        }
    }

    private static string Guid(string id) => System.Guid.TryParse(id, out var g) ? g.ToString("D") : throw new DirectoryWriteException("WriteRefused");
}
