using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Workflow;

namespace PmoReview.Adapters.Outbound;

public sealed record AzureDevOpsOptions
{
    /// <summary>e.g. https://dev.azure.com/contoso</summary>
    public required Uri Organization { get; init; }
    public required string Project { get; init; }
    public string WorkItemType { get; init; } = "Task";
    /// <summary>Process-dependent: Done (Basic/Scrum) or Closed (Agile/CMMI).</summary>
    public string ClosedState { get; init; } = "Done";
}

/// <summary>
/// DISABLED in the pilot (no ADO_ORG_URL). One Azure DevOps work item per request, tagged with the request id:
/// description = the full masked trail (review results, approvals, ID team action, role assignment, justification),
/// history = each new workflow event. Idempotent: an existing item is found by tag before one is created.
/// Auth: the worker's managed identity added to the Azure DevOps organisation (no personal access token).
/// </summary>
public sealed class AzureDevOpsWorkItemTracker(HttpClient http, TokenCredential credential, AzureDevOpsOptions options) : IWorkItemTracker
{
    /// <summary>Azure DevOps resource id (token audience).</summary>
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private const string ApiVersion = "7.1";

    public bool Enabled => true;

    private string ProjectUrl => $"{options.Organization.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(options.Project)}";

    public async Task<PmoReview.Core.Workflow.AdoLink> SyncAsync(WorkItemDraft draft, PmoReview.Core.Workflow.AdoLink? existing, CancellationToken ct)
    {
        var id = existing?.WorkItemId ?? await FindByTagAsync(draft.Tag, ct);
        var ops = new List<object>
        {
            Op("/fields/System.Title", Truncate(draft.Title, 250)),
            Op("/fields/System.Description", Html(draft.Description)),
        };
        JsonDocument doc;
        if (id is null)
        {
            ops.Add(Op("/fields/System.Tags", $"pmo-onboarding; {draft.Tag}"));
            if (draft.NewHistory.Count > 0) ops.Add(Op("/fields/System.History", Html(string.Join("\n", draft.NewHistory))));
            doc = await SendAsync(HttpMethod.Post, $"{ProjectUrl}/_apis/wit/workitems/${Uri.EscapeDataString(options.WorkItemType)}?api-version={ApiVersion}", ops, "application/json-patch+json", ct);
        }
        else
        {
            if (draft.NewHistory.Count > 0) ops.Add(Op("/fields/System.History", Html(string.Join("\n", draft.NewHistory))));
            if (draft.Close) ops.Add(Op("/fields/System.State", options.ClosedState));
            doc = await SendAsync(HttpMethod.Patch, $"{ProjectUrl}/_apis/wit/workitems/{id}?api-version={ApiVersion}", ops, "application/json-patch+json", ct);
        }
        using (doc)
        {
            var newId = doc.RootElement.TryGetProperty("id", out var i) && i.TryGetInt32(out var n) ? n : id ?? throw new OutboundException("AdoMalformed");
            var url = doc.RootElement.TryGetProperty("_links", out var l) && l.TryGetProperty("html", out var h) && h.TryGetProperty("href", out var href)
                ? href.GetString() : $"{ProjectUrl}/_workitems/edit/{newId}";
            return new PmoReview.Core.Workflow.AdoLink { WorkItemId = newId, Url = url };
        }
    }

    private async Task<int?> FindByTagAsync(string tag, CancellationToken ct)
    {
        var query = new { query = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Tags] CONTAINS '{tag.Replace("'", "''")}'" };
        using var doc = await SendAsync(HttpMethod.Post, $"{ProjectUrl}/_apis/wit/wiql?api-version={ApiVersion}", query, "application/json", ct);
        if (!doc.RootElement.TryGetProperty("workItems", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        foreach (var w in items.EnumerateArray())
            if (w.TryGetProperty("id", out var i) && i.TryGetInt32(out var n)) return n;
        return null;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, object body, string contentType, CancellationToken ct)
    {
        if (!url.StartsWith(options.Organization.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) throw new OutboundException("AdoError");
        var json = JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
                using var req = new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new OutboundException("AdoUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new OutboundException("IdentityUnavailable", e);
            }
            using (res)
            {
                if (res.IsSuccessStatusCode)
                    return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var retryable = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (retryable && attempt == 0 && method != HttpMethod.Post)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                throw new OutboundException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "AdoForbidden",
                    HttpStatusCode.NotFound => "AdoNotFound",
                    _ when retryable => "AdoUnavailable",
                    _ => "AdoRejected",
                });
            }
        }
    }

    private static object Op(string path, string value) => new { op = "add", path, value };

    private static string Html(string text) => WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
