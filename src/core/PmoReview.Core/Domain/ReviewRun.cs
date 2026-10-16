using System.Text.Json.Serialization;

namespace PmoReview.Core.Domain;

/// <summary>Frozen copy of a request at Analyze time. Hash covers every field except <see cref="CapturedAt"/>.</summary>
public sealed record RequestSnapshot
{
    public required string RequestId { get; init; }
    public required string ProjectId { get; init; }
    public required int Revision { get; init; }
    public string? RequestType { get; init; }
    public string? Role { get; init; }
    public string? Environment { get; init; }
    public string? Manager { get; init; }
    public string? Justification { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? JoinerUpn { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
}

public sealed record PinnedVersion(string Id, int Version);

public sealed record ReviewRun : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string RequestId { get; init; }
    public required RequestSnapshot Snapshot { get; init; }
    public required string SnapshotHash { get; init; }
    public required int RequestRevision { get; init; }
    public required string InitiatedBy { get; init; }
    public required ProcessingStatus ProcessingStatus { get; init; }
    public Readiness? Readiness { get; init; }
    public IReadOnlyList<PinnedVersion> RuleVersions { get; init; } = [];
    public IReadOnlyList<PinnedVersion> MappingVersions { get; init; } = [];
    public required string ReviewerProvider { get; init; }
    public required string ReviewerVersion { get; init; }
    /// <summary>Null for RuleOnly.</summary>
    public string? PromptVersion { get; init; }
    public Review.ReviewResult? Result { get; init; }
    public required string CorrelationId { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ErrorCategory? ErrorCategory { get; init; }
    public string? RetryOfRunId { get; init; }
    /// <summary>"{requestId}:{revision}:{nonce}". Unique per partition (Cosmos unique key).</summary>
    public required string IdempotencyKey { get; init; }
    /// <summary>Request revision seen at commit time when the run was superseded.</summary>
    public int? SupersededByRevision { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;
}
