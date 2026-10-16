using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Services;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Http;

/// <summary>Item plus its concurrency token, as returned to the SPA.</summary>
public sealed record Item<T>(T Value, string Etag);

public sealed record ProjectSummary(string ProjectId, string Name, IReadOnlyList<AppRole> Roles, bool IsSample);

public sealed record MeResponse(string UserId, string DisplayName, IReadOnlyList<ProjectSummary> Projects);

public sealed record StatusChange(PublicationStatus Status);

public static class Endpoints
{
    public static ApiHost MapCore(this ApiHost host, DataStores stores, RequestService requests, ReferenceDataService reference)
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

        // Requests
        host.Map("GET", "projects/{projectId}/requests", async c =>
            Ok(new { items = (await requests.ListAsync(c.Caller, c["projectId"], c.Ct)).Select(ToItem) }));
        host.Map("POST", "projects/{projectId}/requests", async c =>
            Created(await requests.CreateAsync(c.Caller, c["projectId"], c.Body<RequestInput>(), c.Ct)));
        host.Map("GET", "projects/{projectId}/requests/{requestId}", async c =>
            Ok(await requests.GetAsync(c.Caller, c["projectId"], c["requestId"], c.Ct)));
        host.Map("PUT", "projects/{projectId}/requests/{requestId}", async c =>
            Ok(await requests.UpdateAsync(c.Caller, c["projectId"], c["requestId"], c.Body<RequestInput>(), c.IfMatch, c.Ct)));

        // Reference data
        host.Map("GET", "projects/{projectId}/rules", async c =>
            Ok(new { items = (await reference.ListRulesAsync(c.Caller, c["projectId"], c.Ct)).Select(ToItem) }));
        host.Map("POST", "projects/{projectId}/rules", async c =>
            Created(await reference.CreateRuleDraftAsync(c.Caller, c["projectId"], c.Body<RuleInput>(), c.Ct)));
        host.Map("POST", "projects/{projectId}/rules/{id}/status", async c =>
            Ok(await reference.SetRuleStatusAsync(c.Caller, c["projectId"], c["id"], c.Body<StatusChange>().Status, c.IfMatch, c.Ct)));
        host.Map("GET", "projects/{projectId}/mappings", async c =>
            Ok(new { items = (await reference.ListMappingsAsync(c.Caller, c["projectId"], c.Ct)).Select(ToItem) }));
        host.Map("POST", "projects/{projectId}/mappings", async c =>
            Created(await reference.CreateMappingDraftAsync(c.Caller, c["projectId"], c.Body<MappingInput>(), c.Ct)));
        host.Map("POST", "projects/{projectId}/mappings/{id}/status", async c =>
            Ok(await reference.SetMappingStatusAsync(c.Caller, c["projectId"], c["id"], c.Body<StatusChange>().Status, c.IfMatch, c.Ct)));

        return host;
    }

    public static Item<T> ToItem<T>(Versioned<T> v) => new(v.Value, v.ETag);

    public static ApiResponse Ok(object body) => new(200, body);

    public static ApiResponse Ok<T>(Versioned<T> v) => new(200, ToItem(v), EtagHeader(v.ETag));

    public static ApiResponse Created<T>(Versioned<T> v) => new(201, ToItem(v), EtagHeader(v.ETag));

    public static ApiResponse Accepted(object body) => new(202, body);

    private static Dictionary<string, string> EtagHeader(string etag) => new() { ["ETag"] = etag };
}
