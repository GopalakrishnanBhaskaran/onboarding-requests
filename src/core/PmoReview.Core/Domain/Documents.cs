using System.Text.Json.Serialization;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Domain;

/// <summary>A document stored in one container. <see cref="PartitionKey"/> is the container's partition key value.</summary>
public interface IDocument
{
    string Id { get; }

    [JsonIgnore]
    string PartitionKey { get; }

    /// <summary>
    /// Oldest stored <c>schemaVersion</c> this code reads. Older (v1) documents stay in Cosmos untouched and every
    /// read skips them. 0 = read all (v1 requests still list as read-only legacy rows).
    /// </summary>
    static virtual int MinReadableSchemaVersion => 0;
}

/// <summary>
/// Where notifications for a project go: the contact inventory (Contact inventory page; PMO and Operators edit it).
/// Addresses only (mailboxes or mail-enabled groups); SAMPLE in the pilot.
/// </summary>
public sealed record ProjectContacts
{
    public IReadOnlyList<string> Pmo { get; init; } = [];
    /// <summary>The Security group (mail-enabled) or its members.</summary>
    public IReadOnlyList<string> Security { get; init; } = [];
    public IReadOnlyList<string> IdTeam { get; init; } = [];
    public IReadOnlyList<string> RoleTeam { get; init; } = [];
    public IReadOnlyList<string> Operators { get; init; } = [];
    /// <summary>v1.1 (F3): ProdApprovers' mail (Prod escalation requests). Who may decide stays the ProdApprover role in userAccess.</summary>
    public IReadOnlyList<string> ProdApprovers { get; init; } = [];
    /// <summary>Escalation past SLA (the approver's or team's manager).</summary>
    public string? SecurityManager { get; init; }
    public string? IdTeamManager { get; init; }
    public string? RoleTeamManager { get; init; }
    /// <summary>Used when Entra has no manager for the named Owner.</summary>
    public string? OwnerManagerFallback { get; init; }
}

/// <summary>
/// One project = one Microsoft Entra tenant (e.g. Vestacy, PEST). Tenants are kept apart: every request, mapping row,
/// response record, user role and notification belongs to exactly one project, and a group or user in one tenant says
/// nothing about another, even when the names look alike.
/// </summary>
public sealed record Project : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    /// <summary>Display name of the Entra tenant this project's users, groups and roles live in (e.g. "PEST").</summary>
    public string? TenantName { get; init; }
    /// <summary>
    /// Entra tenant (directory) id. Null = the AI agent's own tenant. When set, the agent reads and writes Entra only if it
    /// equals <see cref="Workflow.WorkflowSettings.AgentTenantId"/>; otherwise the teams do it in that tenant.
    /// </summary>
    public string? TenantId { get; init; }
    /// <summary>Environments a request can target, in order (e.g. Dev, PreProd, Prod). Empty = no environment field.</summary>
    public IReadOnlyList<string> Environments { get; init; } = [];
    /// <summary>Appended to the normalised Enterprise ID to derive the domain-specific ID, e.g. "@contoso.onmicrosoft.com".</summary>
    public string? DomainIdSuffix { get; init; }
    /// <summary>Entra group naming convention (regex, whole name). Checked in Stage 2 and when the ID team reports.</summary>
    public string? GroupNamePattern { get; init; }
    public ProjectContacts Contacts { get; init; } = new();
    /// <summary>Contact inventory version: 0 = never saved in the app (seeded or imported), +1 on every saved change.</summary>
    public int ContactsVersion { get; init; }
    /// <summary>
    /// v1.1: the tenant is production. Offboarding there needs a second person to confirm (requester ≠ confirmer).
    /// Independent of a request's environment (Dev/Test/UAT/PreProd/Prod), which decides the Prod read-only rule.
    /// </summary>
    public bool IsProductionTenant { get; init; }
    /// <summary>Who saved the contact inventory last (sign-in name) and when.</summary>
    public string? ContactsUpdatedBy { get; init; }
    public DateTimeOffset? ContactsUpdatedAt { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 2;
    [JsonIgnore] public string PartitionKey => ProjectId;
}

/// <summary>
/// One row of the PMO-to-Entra Group Mapping Sheet: PMO resource + access level → Entra group → role.
/// The sheet is a source of truth owned by PMO; it is read fresh on every request and never cached.
/// <see cref="Version"/> increases on every change, so a review can tell the sheet changed after it.
/// </summary>
/// <summary>Workbook colour coding: Red = wrong or undecided (never act on it), Amber = usable with a caveat.</summary>
public enum RowFlag { None, Amber, Red }

/// <summary>
/// One Azure role the group carries, as the response document states it: role name, the resource it applies to and the
/// resolved Azure scope. The AI agent assigns only grants with a role definition id and a scope on its allow-list.
/// </summary>
public sealed record AzureRoleGrant
{
    /// <summary>Role name as written in the response document, e.g. "Storage Blob Data Reader".</summary>
    public required string RoleName { get; init; }
    /// <summary>Role definition GUID (built-in names are resolved by <see cref="BuiltInRoles"/>).</summary>
    public string? RoleDefinitionId { get; init; }
    /// <summary>Azure scope (<c>/subscriptions/…</c>). Null = the resource name could not be resolved: the role team assigns it.</summary>
    public string? Scope { get; init; }
    /// <summary>Resource name from the response document, e.g. a storage account or resource group.</summary>
    public string? ResourceName { get; init; }
    /// <summary>e.g. Microsoft.Storage/storageAccounts, Microsoft.Resources/resourceGroups.</summary>
    public string? ResourceType { get; init; }

    /// <summary>"Role at scope" as the role team and the verification read it.</summary>
    [JsonIgnore] public string Where => Scope ?? ResourceName ?? "";

    /// <summary>The role definition id, from the grant or from the built-in role name.</summary>
    [JsonIgnore] public string? RoleId => RoleDefinitionId?.Trim().ToLowerInvariant() ?? BuiltInRoles.IdOf(RoleName);

    public bool SameAs(AzureRoleGrant o) =>
        string.Equals(RoleId ?? RoleName.Trim(), o.RoleId ?? o.RoleName.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Where.Trim().TrimEnd('/'), o.Where.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

public sealed record MappingRow : IDocument
{
    /// <summary>Same as <see cref="RowId"/>.</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string RowId { get; init; }
    public required string Resource { get; init; }
    public required string AccessLevel { get; init; }
    public required string EntraGroup { get; init; }
    public required string Role { get; init; }
    /// <summary>Where the role applies (e.g. an Azure scope or a Databricks workspace). Optional.</summary>
    public string? RoleScope { get; init; }
    /// <summary>Optional: the Azure role definition (GUID) the AI agent assigns to the group after both approvals.</summary>
    public string? AzureRoleDefinitionId { get; init; }
    /// <summary>Optional: the Azure scope (<c>/subscriptions/…</c>) for <see cref="AzureRoleDefinitionId"/>.</summary>
    public string? AzureScope { get; init; }
    /// <summary>
    /// Every Azure role the group carries (response document: role × resource). Takes precedence over the single
    /// <see cref="AzureRoleDefinitionId"/> / <see cref="AzureScope"/> pair.
    /// </summary>
    public IReadOnlyList<AzureRoleGrant> AzureRoles { get; init; } = [];
    /// <summary>Environment of the group (e.g. Dev, PreProd). Null = the row applies to every environment.</summary>
    public string? Environment { get; init; }
    /// <summary>Entra object id of the group, when known. Names get typos and trailing spaces; ids do not.</summary>
    public string? GroupObjectId { get; init; }
    /// <summary>Job roles the group is meant for (e.g. "Application / Production Support").</summary>
    public string? JobRoles { get; init; }
    public RowFlag Flag { get; init; }
    /// <summary>Caveats and open questions from the workbook (shown to approvers; Red blocks the review).</summary>
    public string? Notes { get; init; }
    public bool Privileged { get; init; }
    public bool SensitiveResource { get; init; }
    public bool Deprecated { get; init; }
    public GroupState GroupState { get; init; } = GroupState.Exists;
    // v1.1 mapping sheet columns (I1; uploaded by the DataMaintainer). Optional on rows imported before 30 Oct 2026.
    /// <summary>I1 TenantId: must equal the project's tenant on upload.</summary>
    public string? TenantId { get; init; }
    /// <summary>I1 Domain (business domain, e.g. Finance). A Resource* option.</summary>
    public string? Domain { get; init; }
    /// <summary>I1 ResourceGroupType: Domain | PH | ACQ (<see cref="ResourceGroupTypes"/>).</summary>
    public string? ResourceGroupType { get; init; }
    /// <summary>I1 Owner of the mapping (sign-in name).</summary>
    public string? Owner { get; init; }
    public DateOnly? EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    /// <summary>Retired by an upload with "Retire missing rows" (kept, never deleted; never offered or granted).</summary>
    public bool Retired { get; init; }
    /// <summary>The mapping version (upload) that last changed this row.</summary>
    public int? MappingVersion { get; init; }
    /// <summary>Row number in the uploaded file (for mapping quality messages).</summary>
    public int? SheetRow { get; init; }
    public required string SourceReference { get; init; }
    public required int Version { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 2;
    [JsonIgnore] public string PartitionKey => ProjectId;

    /// <summary>The Azure roles of this row: <see cref="AzureRoles"/>, else the single legacy pair, else none.</summary>
    public IReadOnlyList<AzureRoleGrant> Grants() =>
        AzureRoles.Count > 0 ? AzureRoles
        : AzureRoleDefinitionId is not null && AzureScope is not null
            ? [new AzureRoleGrant { RoleName = BuiltInRoles.NameOf(AzureRoleDefinitionId) ?? AzureRoleDefinitionId, RoleDefinitionId = AzureRoleDefinitionId, Scope = AzureScope }]
            : [];
}

/// <summary>Built-in Azure role names → role definition ids (Microsoft Learn, built-in roles, checked 21 Oct 2026; VERIFIED_FACTS F62).</summary>
public static class BuiltInRoles
{
    public static readonly IReadOnlyDictionary<string, string> Ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Reader"] = "acdd72a7-3385-48ef-bd42-f606fba81ae7",
        ["Storage Blob Data Reader"] = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1",
        ["Storage Blob Data Contributor"] = "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
        ["Storage Blob Data Owner"] = "b7e6dc6d-f1e8-4753-8033-0f276bb0955b",
        ["Key Vault Reader"] = "21090545-7ca7-4776-b22c-e363652d74d2",
        ["Key Vault Secrets User"] = "4633458b-17de-408a-b874-0445c86b69e6",
        ["Key Vault Secrets Officer"] = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
    };

    public static string? IdOf(string? roleName) =>
        roleName is not null && Ids.TryGetValue(roleName.Trim(), out var id) ? id : null;

    public static string? NameOf(string? roleId) =>
        roleId is null ? null : Ids.FirstOrDefault(p => string.Equals(p.Value, roleId.Trim(), StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>
    /// Never assigned by the AI agent, whatever its allow-list says: Azure's privileged administrator roles (VERIFIED_FACTS
    /// F68) and Key Vault Data Access Administrator, which can assign roles itself. People handle these.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NeverByAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Owner"] = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635",
        ["Contributor"] = "b24988ac-6180-42a0-ab88-20f7382dd24c",
        ["Reservations Administrator"] = "a8889054-8d42-49c9-bc1c-52486c10e7cd",
        ["Role Based Access Control Administrator"] = "f58310d9-a9f6-439a-9e8d-f62e7b41a168",
        ["User Access Administrator"] = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9",
        ["Key Vault Data Access Administrator"] = "8b54135c-b56d-4d72-a534-26097cfdc8d8",
    };

    /// <summary>By role id or by name (a grant whose id is not resolved still counts).</summary>
    public static bool IsNeverByAgent(AzureRoleGrant g) =>
        NeverByAgent.ContainsKey(g.RoleName.Trim())
        || (g.RoleId is { } id && NeverByAgent.Values.Contains(id, StringComparer.OrdinalIgnoreCase));
}

public enum ResponseState { PendingApproval, Approved, Assigned, Rejected, Revoked }

public sealed record AssignedRole(string Role, string? Scope);

/// <summary>
/// One row of the Response Document: the role assignment record for a request (user, group, role, approvals,
/// justification). Source of truth for Stage 5. The agent writes what was reviewed and approved; the role assignment
/// team records what it assigned. Read fresh on every request.
/// </summary>
public sealed record ResponseRecord : IDocument
{
    /// <summary>Same as the request id: one record per request.</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string RequestId { get; init; }
    /// <summary>Domain-specific ID of the user.</summary>
    public required string UserId { get; init; }
    public required string Resource { get; init; }
    public required string AccessLevel { get; init; }
    public string? Environment { get; init; }
    public required string EntraGroup { get; init; }
    public required string Role { get; init; }
    public string? RoleScope { get; init; }
    /// <summary>The Azure roles that were reviewed and approved for the group (role × resource).</summary>
    public IReadOnlyList<AzureRoleGrant> AzureRoles { get; init; } = [];
    public bool Privileged { get; init; }
    public required ResponseState State { get; init; }
    public required string Justification { get; init; }
    public string? OwnerApprovedBy { get; init; }
    public DateTimeOffset? OwnerApprovedAt { get; init; }
    public string? SecurityApprovedBy { get; init; }
    public DateTimeOffset? SecurityApprovedAt { get; init; }
    public IReadOnlyList<AssignedRole> AssignedRoles { get; init; } = [];
    public string? AssignedBy { get; init; }
    public DateTimeOffset? AssignedAt { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required int Version { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 2;
    [JsonIgnore] public string PartitionKey => ProjectId;

    /// <summary>Counts against duplicate/conflict checks and broad-access flags.</summary>
    [JsonIgnore] public bool IsActive => State is ResponseState.PendingApproval or ResponseState.Approved or ResponseState.Assigned;
}

/// <summary>
/// An onboarding request. Intake fields are edited by the requester (only while Returned); <see cref="Flow"/> is
/// written only by the workflow (approvals, team actions, the agent). Minimum personal data only.
/// </summary>
public sealed record AccessRequest : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    /// <summary>The joiner's enterprise sign-in or mail address (e.g. jane.doe@company.com). Notifications to the user go here.</summary>
    public string? EnterpriseId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    /// <summary>Derived by the server from the Enterprise ID and the project's domain. Never typed by a user.</summary>
    public string? DomainId { get; init; }
    public string? Resource { get; init; }
    public string? AccessLevel { get; init; }
    /// <summary>Target environment (one of the project's environments), e.g. Dev or PreProd.</summary>
    public string? Environment { get; init; }
    public string? Justification { get; init; }
    /// <summary>Sign-in name of the business Owner. Only this person's approval counts at the Owner stage.</summary>
    public string? Owner { get; init; }
    /// <summary>External (guest) user. A risk flag for approvers.</summary>
    public bool Guest { get; init; }
    /// <summary>v1.1 (F1): requests submitted together from one Resource* selection share this id (one request per resolved group).</summary>
    public string? BundleId { get; init; }
    public required string CreatedBy { get; init; }
    /// <summary>Requester's sign-in name (notifications).</summary>
    public string? CreatedByName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Server-maintained. Incremented on every update.</summary>
    public required int Revision { get; init; }
    /// <summary>Workflow state. Null only on legacy v1 documents (read-only in v2).</summary>
    public WorkflowState? Flow { get; init; }
    public int SchemaVersion { get; init; } = 2;
    [JsonIgnore] public string PartitionKey => ProjectId;
}

public sealed record UserAccess : IDocument
{
    /// <summary>"{userId}:{projectId}"</summary>
    public required string Id { get; init; }
    /// <summary>SWA client principal userId, or "signin:&lt;sign-in name&gt;" for a registration by sign-in name (AccessService).</summary>
    public required string UserId { get; init; }
    public required string ProjectId { get; init; }
    public required IReadOnlyList<AppRole> Roles { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => UserId;

    public static string MakeId(string userId, string projectId) => $"{userId}:{projectId}";
}
