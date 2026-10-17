using System.Globalization;
using System.Text.Json;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Processing;
using PmoReview.Core.Services;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Http;

/// <summary>Item plus its concurrency token, as returned to the SPA.</summary>
public sealed record Item<T>(T Value, string Etag);

public sealed record ProjectSummary(string ProjectId, string Name, IReadOnlyList<AppRole> Roles, bool IsSample);

public sealed record MeResponse(string UserId, string DisplayName, IReadOnlyList<ProjectSummary> Projects);

public static class Endpoints
{
    public static ApiHost MapCore(this ApiHost host, DataStores stores, WorkflowService workflow, MappingSheetService sheet)
    {
        host.Map("GET", "me", async c =>
        {
            var projects = new List<ProjectSummary>();
            foreach (var (projectId, roles) in c.Caller.Grants.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var p = await stores.Projects.GetAsync(projectId, projectId, c.Ct);
                projects.Add(new ProjectSummary(projectId, p?.Value.Name ?? projectId, roles.Order().ToList(), p?.Value.IsSample ?? false));
            }
            return Ok(new MeResponse(c.Caller.UserId, c.Caller.DisplayName, projects));
        });
        host.Map("GET", "queue", async c => Ok(new { items = await workflow.MyActionsAsync(c.Caller, c.Ct) }));

        // Requests (Stage 1) and status tracking.
        host.Map("GET", "projects/{projectId}/requests", async c =>
            Ok(new { items = await workflow.ListAsync(c.Caller, c["projectId"], c.Ct), flowOverview = StatusText.FlowOverview, whoDoesWhat = Messages.WhoDoesWhat }));
        host.Map("POST", "projects/{projectId}/requests", async c =>
            new ApiResponse(201, await workflow.SubmitAsync(c.Caller, c["projectId"], c.Body<RequestInput>(), c.Request.CorrelationId, c.Ct)));
        host.Map("GET", "projects/{projectId}/requests/{requestId}", async c =>
            Ok(await workflow.ViewAsync(c.Caller, c["projectId"], c["requestId"], c.Ct)));
        host.Map("PUT", "projects/{projectId}/requests/{requestId}", async c =>
            Ok(await workflow.ResubmitAsync(c.Caller, c["projectId"], c["requestId"], c.Body<RequestInput>(), c.IfMatch, c.Request.CorrelationId, c.Ct)));
        host.Map("GET", "projects/{projectId}/requests/{requestId}/trail", async c =>
            Ok(new { text = await workflow.TrailAsync(c.Caller, c["projectId"], c["requestId"], c.Ct) }));

        // Stage 3: approvals (the tracked channel). Stage 4/5: team tasks.
        host.Map("POST", "projects/{projectId}/requests/{requestId}/approvals", async c =>
            Ok(await workflow.ApproveAsync(c.Caller, c["projectId"], c["requestId"], c.Body<ApprovalInput>(), c.Request.CorrelationId, c.Ct)));
        host.Map("POST", "projects/{projectId}/requests/{requestId}/tasks/{team}/pickup", async c =>
            Ok(await workflow.PickUpAsync(c.Caller, c["projectId"], c["requestId"], WorkflowService.TeamFromRoute(c["team"]), c.Ct)));
        host.Map("POST", "projects/{projectId}/requests/{requestId}/tasks/{team}/report", async c =>
        {
            var team = WorkflowService.TeamFromRoute(c["team"]);
            if (team == AppRole.IdTeam)
            {
                RejectPassInBody(c.Request.Body);
                return Ok(await workflow.ReportIdTeamAsync(c.Caller, c["projectId"], c["requestId"], c.Body<IdTeamReportInput>(), c.Request.CorrelationId, c.Ct));
            }
            return Ok(await workflow.ReportRoleTeamAsync(c.Caller, c["projectId"], c["requestId"], c.Body<RoleTeamReportInput>(), c.Request.CorrelationId, c.Ct));
        });
        host.Map("POST", "projects/{projectId}/requests/{requestId}/advance", async c =>
            Ok(await workflow.AdvanceAsync(c.Caller, c["projectId"], c["requestId"], c.Request.CorrelationId, c.Ct)));

        // Sources of truth.
        host.Map("GET", "projects/{projectId}/mapping-sheet", async c =>
            Ok(new { items = (await sheet.ListAsync(c.Caller, c["projectId"], c.Ct)).Select(ToItem) }));
        host.Map("POST", "projects/{projectId}/mapping-sheet", async c =>
            Created(await sheet.CreateAsync(c.Caller, c["projectId"], c.Body<MappingRowInput>(), c.Ct)));
        host.Map("PUT", "projects/{projectId}/mapping-sheet/{rowId}", async c =>
            Ok(await sheet.UpdateAsync(c.Caller, c["projectId"], c["rowId"], c.Body<MappingRowInput>(), c.IfMatch, c.Ct)));
        host.Map("POST", "projects/{projectId}/mapping-sheet/{rowId}/group", async c =>
            Ok(await sheet.UpdateGroupAsync(c.Caller, c["projectId"], c["rowId"], c.Body<MappingGroupInput>(), c.IfMatch, c.Ct)));
        host.Map("GET", "projects/{projectId}/response-document", async c =>
            Ok(new { items = (await sheet.ListResponseDocumentAsync(c.Caller, c["projectId"], c.Ct)).Select(ToItem) }));

        // Issues and reports.
        host.Map("GET", "projects/{projectId}/issues", async c =>
            Ok(new { items = await workflow.IssuesAsync(c.Caller, c["projectId"], c.Ct) }));
        host.Map("GET", "projects/{projectId}/reports/daily/{date}", async c =>
        {
            if (!DateOnly.TryParseExact(c["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw AppException.Invalid("date must be yyyy-MM-dd.");
            return Ok(await workflow.DailyAsync(c.Caller, c["projectId"], date, c.Ct));
        });
        host.Map("GET", "projects/{projectId}/reports/developer", async c =>
            Ok(await workflow.DeveloperAsync(c.Caller, c["projectId"], c.Ct)));
        return host;
    }

    public static ApiHost MapRuns(this ApiHost host, RunService runs)
    {
        host.Map("GET", "projects/{projectId}/requests/{requestId}/runs", async c =>
            Ok(new { items = (await runs.ListForRequestAsync(c.Caller, c["projectId"], c["requestId"], c.Ct)).Select(ToItem) }));
        host.Map("GET", "projects/{projectId}/runs/{runId}", async c =>
            Ok(await runs.GetAsync(c.Caller, c["projectId"], c["runId"], c.Ct)));
        host.Map("POST", "projects/{projectId}/runs/{runId}/retry", async c =>
            Accepted(await runs.RetryAsync(c.Caller, c["projectId"], c["runId"], c.Body<RetryInput>(), c.Request.CorrelationId, c.Ct)));
        host.Map("POST", "projects/{projectId}/operator/recover", async c =>
            Ok(new { recovered = await runs.RecoverStuckAsync(c.Caller, c["projectId"], c.Ct) }));
        return host;
    }

    private static readonly HashSet<string> TapFields = new(StringComparer.OrdinalIgnoreCase) { "issuedAt", "expiresAt", "oneTime", "issuedBy" };
    private static readonly string[] SecretWords = ["pass", "password", "secret", "code", "value", "temporaryaccesspass", "accesspass", "otp"];

    /// <summary>The Temporary Access Pass must never reach this app: refuse a body that carries anything that could be it.</summary>
    private static void RejectPassInBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (SecretWords.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) throw PassRefused();
                if (string.Equals(p.Name, "tap", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.EnumerateObject().Any(x => !TapFields.Contains(x.Name)))
                    throw PassRefused();
            }
        }
        catch (JsonException)
        {
            // RouteContext.Body reports invalid JSON.
        }
    }

    private static AppException PassRefused() =>
        AppException.Invalid("Never send the Temporary Access Pass to this app. Record only issued time, expiry, one-time flag and issued-by.");

    public static Item<T> ToItem<T>(Versioned<T> v) => new(v.Value, v.ETag);

    public static ApiResponse Ok(object body) => new(200, body);

    public static ApiResponse Ok<T>(Versioned<T> v) => new(200, ToItem(v), EtagHeader(v.ETag));

    public static ApiResponse Created<T>(Versioned<T> v) => new(201, ToItem(v), EtagHeader(v.ETag));

    public static ApiResponse Accepted(object body) => new(202, body);

    private static Dictionary<string, string> EtagHeader(string etag) => new() { ["ETag"] = etag };
}
