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
    // v1.1 (30 Oct 2026)
    /// <summary>F4: mapping sheet upload versions (+ before-images for rollback).</summary>
    public const string MappingVersions = "mappingVersions";
    /// <summary>F4/F2: the people inventory (I2). Checks the TAP recipient (and is its fallback, <see cref="Workflow.TapRecipient"/>).</summary>
    public const string PeopleInventory = "peopleInventory";
    /// <summary>F4: people inventory upload versions.</summary>
    public const string InventoryVersions = "inventoryVersions";
    /// <summary>F1/F2/F3/F5: decisions for people (Prod writes, unresolved groups, TAP recipient, offboarding leftovers).</summary>
    public const string Escalations = "escalations";
    /// <summary>F1: near-duplicate group names on the mapping sheet.</summary>
    public const string MappingQualityIssues = "mappingQualityIssues";
    /// <summary>F1: the worker's last Entra read of each mapping group name.</summary>
    public const string GroupResolutions = "groupResolutions";
    /// <summary>F5: offboarding runs (plan, confirm, execute, verify).</summary>
    public const string OffboardingRuns = "offboardingRuns";
    /// <summary>Append-only audit trail of v1.1 actions.</summary>
    public const string AuditEvents = "auditEvents";
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
    IDocumentStore<Issue> Issues,
    IDocumentStore<DataVersion> MappingVersions,
    IDocumentStore<Person> PeopleInventory,
    IDocumentStore<DataVersion> InventoryVersions,
    IDocumentStore<Escalation> Escalations,
    IDocumentStore<MappingQualityIssue> MappingQualityIssues,
    IDocumentStore<GroupResolution> GroupResolutions,
    IDocumentStore<OffboardingRun> OffboardingRuns,
    IDocumentStore<AuditEvent> AuditEvents)
{
    /// <summary>The version store of one uploadable data set.</summary>
    public IDocumentStore<DataVersion> VersionsOf(DataKind kind) => kind == DataKind.Mapping ? MappingVersions : InventoryVersions;
}
