using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

public enum NotificationKind
{
    FlowOverview,
    ReviewReturned,
    ApprovalRequested,
    Reminder,
    Escalation,
    Rejected,
    TeamTask,
    TaskReturned,
    TapCleanupNeeded,
    AccessReady,
    CompletionSummary,
    PmoInformed,
    Closure,
    SecurityAlert,
    DailySummary,
    DeveloperReport,
}

/// <summary>Pending → Sent (mail sender) or Recorded (pilot: kept in the app only) or Failed (after 3 attempts).</summary>
public enum NotificationStatus { Pending, Sent, Recorded, Failed }

/// <summary>
/// Outbox entry. Created once per idempotency key (request id + step) and kind; the sender marks it.
/// Subject and body are masked when created and never contain a Temporary Access Pass.
/// </summary>
public sealed record Notification : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public string? RequestId { get; init; }
    public required NotificationKind Kind { get; init; }
    public required string IdempotencyKey { get; init; }
    public required IReadOnlyList<string> To { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required NotificationStatus Status { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? LastError { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(string idempotencyKey, NotificationKind kind) => Ids.Hash($"{idempotencyKey}|{kind}");
}

public enum IssueKind
{
    /// <summary>Review passed but the ID team created/used a different group name.</summary>
    GroupNameMismatch,
    /// <summary>Role assigned does not match the response document.</summary>
    RoleMismatch,
    /// <summary>Approval attempt by someone who is not the named Owner / a Security member. Ignored, logged, Security alerted.</summary>
    UnauthorizedApproval,
    ApprovalSlaBreached,
    /// <summary>ID team or role team task not picked up past SLA.</summary>
    TaskSlaBreached,
    /// <summary>Mapping sheet changed after the AI review; the review is re-run before continuing.</summary>
    MappingChangedAfterReview,
    VerificationMismatch,
    TapNotDeleted,
    DependencyFailure,
    AutoFixApplied,
    NeedsDeveloper,
}

public enum IssueSeverity { Info, Warning, High }

public enum IssueStatus { Open, AutoFixed, Resolved }

/// <summary>Issues list entry. Id = request id + step + kind, so repeats only count occurrences.</summary>
public sealed record Issue : IDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public string? RequestId { get; init; }
    public required IssueKind Kind { get; init; }
    public required string Step { get; init; }
    public required IssueSeverity Severity { get; init; }
    public required string Summary { get; init; }
    public string? Detail { get; init; }
    public required IssueStatus Status { get; init; }
    public int Occurrences { get; init; } = 1;
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(string? requestId, string step, IssueKind kind) => Ids.Hash($"{requestId ?? "-"}|{step}|{kind}");
}

public static class Ids
{
    /// <summary>Stable, storage-safe id for an idempotency key (no '/', '\\', '?', '#').</summary>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>Idempotency key = request id + step.</summary>
    public static string Key(string requestId, string step) => $"{requestId}:{step}";
}
