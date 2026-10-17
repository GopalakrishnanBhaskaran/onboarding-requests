using System.Text.Json.Serialization;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Domain;

/// <summary>A document stored in one container. <see cref="PartitionKey"/> is the container's partition key value.</summary>
public interface IDocument
{
    string Id { get; }

    [JsonIgnore]
    string PartitionKey { get; }
}

/// <summary>Where notifications for a project go. Addresses only (mailboxes or mail-enabled groups); SAMPLE in the pilot.</summary>
public sealed record ProjectContacts
{
    public IReadOnlyList<string> Pmo { get; init; } = [];
    /// <summary>The Security group (mail-enabled) or its members.</summary>
    public IReadOnlyList<string> Security { get; init; } = [];
    public IReadOnlyList<string> IdTeam { get; init; } = [];
    public IReadOnlyList<string> RoleTeam { get; init; } = [];
    public IReadOnlyList<string> Operators { get; init; } = [];
    /// <summary>Escalation past SLA (the approver's or team's manager).</summary>
    public string? SecurityManager { get; init; }
    public string? IdTeamManager { get; init; }
    public string? RoleTeamManager { get; init; }
    /// <summary>Used when Entra has no manager for the named Owner.</summary>
    public string? OwnerManagerFallback { get; init; }
}

public sealed record Project : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    /// <summary>Appended to the normalised Enterprise ID to derive the domain-specific ID, e.g. "@contoso.onmicrosoft.com".</summary>
    public string? DomainIdSuffix { get; init; }
    /// <summary>Entra group naming convention (regex, whole name). Checked in Stage 2 and when the ID team reports.</summary>
    public string? GroupNamePattern { get; init; }
    public ProjectContacts Contacts { get; init; } = new();
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 2;
    [JsonIgnore] public string PartitionKey => ProjectId;
}

/// <summary>
/// One row of the PMO-to-Entra Group Mapping Sheet: PMO resource + access level → Entra group → role.
/// The sheet is a source of truth owned by PMO; it is read fresh on every request and never cached.
/// <see cref="Version"/> increases on every change, so a review can tell the sheet changed after it.
/// </summary>
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
    public bool Privileged { get; init; }
    public bool SensitiveResource { get; init; }
    public bool Deprecated { get; init; }
    public GroupState GroupState { get; init; } = GroupState.Exists;
    public required string SourceReference { get; init; }
    public required int Version { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 2;
    [JsonIgnore] public string PartitionKey => ProjectId;
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
    public required string EntraGroup { get; init; }
    public required string Role { get; init; }
    public string? RoleScope { get; init; }
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
    public string? EnterpriseId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    /// <summary>Derived by the server from the Enterprise ID and the project's domain. Never typed by a user.</summary>
    public string? DomainId { get; init; }
    public string? Resource { get; init; }
    public string? AccessLevel { get; init; }
    public string? Justification { get; init; }
    /// <summary>Sign-in name of the business Owner. Only this person's approval counts at the Owner stage.</summary>
    public string? Owner { get; init; }
    /// <summary>External (guest) user. A risk flag for approvers.</summary>
    public bool Guest { get; init; }
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
    /// <summary>SWA client principal userId.</summary>
    public required string UserId { get; init; }
    public required string ProjectId { get; init; }
    public required IReadOnlyList<AppRole> Roles { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => UserId;

    public static string MakeId(string userId, string projectId) => $"{userId}:{projectId}";
}
