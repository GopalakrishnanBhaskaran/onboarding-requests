using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Storage;

public static class ContainerNames
{
    public const string Projects = "projects";
    public const string Requests = "requests";
    public const string ReviewRuns = "reviewRuns";
    public const string UserAccess = "userAccess";
    /// <summary>PMO-to-Entra Group Mapping Sheet (source of truth).</summary>
    public const string MappingSheet = "mappingSheet";
    /// <summary>Response Document: role assignment records (source of truth).</summary>
    public const string ResponseDocument = "responseDocument";
    public const string Notifications = "notifications";
    public const string Issues = "issues";
}

/// <summary>All containers the application uses. v1 containers no longer read by v2 (requirementRules, roleMappings,
/// reviewDecisions, handoffs, joinerPasses) are left in place; nothing deletes them.</summary>
public sealed record DataStores(
    IDocumentStore<Project> Projects,
    IDocumentStore<AccessRequest> Requests,
    IDocumentStore<ReviewRun> Runs,
    IDocumentStore<UserAccess> UserAccess,
    IDocumentStore<MappingRow> MappingSheet,
    IDocumentStore<ResponseRecord> ResponseDocument,
    IDocumentStore<Notification> Notifications,
    IDocumentStore<Issue> Issues);
