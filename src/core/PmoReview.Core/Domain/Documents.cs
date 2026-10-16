using System.Text.Json.Serialization;

namespace PmoReview.Core.Domain;

/// <summary>A document stored in one container. <see cref="PartitionKey"/> is the container's partition key value.</summary>
public interface IDocument
{
    string Id { get; }

    [JsonIgnore]
    string PartitionKey { get; }
}

public sealed record Project : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> SupportedEnvironments { get; init; } = [];
    public IReadOnlyList<string> SupportedRequestTypes { get; init; } = [];
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;
}

public sealed record RequirementRule : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string RuleId { get; init; }
    public required int Version { get; init; }
    public required string RequestType { get; init; }
    public required string Field { get; init; }
    public required RuleKind Rule { get; init; }
    public string? Parameter { get; init; }
    public required string RequirementText { get; init; }
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public required string Owner { get; init; }
    public required PublicationStatus PublicationStatus { get; init; }
    public required string SourceReference { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public bool IsEffectiveOn(DateOnly date) =>
        PublicationStatus == PublicationStatus.Published
        && EffectiveFrom <= date
        && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>
/// Reference data. <see cref="GroupReference"/> is a label, never used to assign anything.
/// <see cref="AccessPackageId"/> (optional) links the mapping to a Microsoft Entra access package; Entra, not this app,
/// approves and assigns. A review can only be handed off when its pinned mapping version has one.
/// </summary>
public sealed record RoleMapping : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string MappingId { get; init; }
    public required int Version { get; init; }
    public required string BusinessRole { get; init; }
    public required string Environment { get; init; }
    public required string GroupReference { get; init; }
    /// <summary>Entra access package object id (GUID). Null = no Entra hand-off for this mapping.</summary>
    public string? AccessPackageId { get; init; }
    /// <summary>The access package's "My Access portal link", given to the new joiner to request access.</summary>
    public string? MyAccessLink { get; init; }
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public required string Owner { get; init; }
    public required PublicationStatus PublicationStatus { get; init; }
    public required string SourceReference { get; init; }
    public bool IsSample { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public bool IsEffectiveOn(DateOnly date) =>
        PublicationStatus == PublicationStatus.Published
        && EffectiveFrom <= date
        && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>An onboarding/access request. Minimum personal data only.</summary>
public sealed record AccessRequest : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public string? RequestType { get; init; }
    public string? Role { get; init; }
    public string? Environment { get; init; }
    public string? Manager { get; init; }
    public string? Justification { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    /// <summary>Sign-in name (UPN) of the person who will get access. Used only to match the Entra request; never looked up or created here.</summary>
    public string? JoinerUpn { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Server-maintained. Incremented on every update.</summary>
    public required int Revision { get; init; }
    public int SchemaVersion { get; init; } = 1;
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

/// <summary>Feedback on review quality. Not an access approval.</summary>
public sealed record ReviewDecision : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ReviewRunId { get; init; }
    public required string Reviewer { get; init; }
    public required DecisionOutcome Outcome { get; init; }
    public string? Comment { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;
}
