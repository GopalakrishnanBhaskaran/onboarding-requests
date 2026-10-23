using System.Text.Json.Serialization;

namespace PmoReview.Core.Domain;

/// <summary>Frozen copy of a request at review time. Hash covers every field except <see cref="CapturedAt"/>.</summary>
public sealed record RequestSnapshot
{
    public required string RequestId { get; init; }
    public required string ProjectId { get; init; }
    public required int Revision { get; init; }
    public string? EnterpriseId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? DomainId { get; init; }
    public string? Resource { get; init; }
    public string? AccessLevel { get; init; }
    public string? Environment { get; init; }
    public string? Justification { get; init; }
    public string? Owner { get; init; }
    public bool Guest { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
}

public sealed record PinnedVersion(string Id, int Version);

/// <summary>Model token usage for one run (cost tracking). Null for the rule-only reviewer.</summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int Calls);

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
    public ReviewOutcome? Outcome { get; init; }
    /// <summary>Mapping rows (id + version) the review read for this resource.</summary>
    public IReadOnlyList<PinnedVersion> MappingRows { get; init; } = [];
    /// <summary>Fingerprint of the mapping-sheet rows for the requested resource at review time.</summary>
    public string? SheetFingerprint { get; init; }
    public required string ReviewerProvider { get; init; }
    public required string ReviewerVersion { get; init; }
    /// <summary>Null for RuleOnly.</summary>
    public string? PromptVersion { get; init; }
    public TokenUsage? Usage { get; init; }
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
    /// <summary>Why the run was started (submit, resubmit, mapping-changed, retry, operator).</summary>
    public string? Trigger { get; init; }
    public int SchemaVersion { get; init; } = 2;
    /// <summary>v1 runs (schemaVersion 1) are still in the live container with a v1 result the v2 shape cannot read.</summary>
    public static int MinReadableSchemaVersion => 2;
    [JsonIgnore] public string PartitionKey => ProjectId;
}
