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
    /// <summary>What the AI agent created after both approvals (requester, Security; the ID team when the pass was mailed).</summary>
    ProvisioningSummary,
    /// <summary>
    /// Agent TAP: the joiner's sign-in name, initial password, one-time TAP and MFA set-up steps, sent once to the verified
    /// enterprise mail. v1.1 F2: never stored (an audit event records it).
    /// </summary>
    SignInDetails,
    /// <summary>v1.1 F5: offboarding verified complete — mailed to the project's PMO only.</summary>
    OffboardingCompleted,
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
    /// <summary>Where the mail actually went (differs from <see cref="To"/> only in a test environment's redirect).</summary>
    public IReadOnlyList<string>? DeliveredTo { get; init; }
    public string? LastError { get; init; }
    /// <summary>Entries left out because they are not mail addresses (e.g. a sign-in name without a mailbox), masked.</summary>
    public IReadOnlyList<string>? Skipped { get; init; }
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(string idempotencyKey, NotificationKind kind) => Ids.Hash($"{idempotencyKey}|{kind}");

    /// <summary>
    /// Send order when the mail limit holds mails back (F70): asks for a decision or an action first, then outcomes, then
    /// information. Lower goes first; equal kinds go oldest first.
    /// </summary>
    public static int Priority(NotificationKind kind) => kind switch
    {
        NotificationKind.SecurityAlert or NotificationKind.ApprovalRequested or NotificationKind.TeamTask or NotificationKind.TaskReturned
            or NotificationKind.Escalation or NotificationKind.SignInDetails => 0,
        NotificationKind.AccessReady or NotificationKind.CompletionSummary or NotificationKind.Rejected or NotificationKind.ReviewReturned
            or NotificationKind.Reminder or NotificationKind.DeveloperReport => 1,
        NotificationKind.FlowOverview or NotificationKind.ProvisioningSummary => 2,
        _ => 3,
    };

    /// <summary>Kinds that ask a team or approver to act: without any address they are a gap in the contact inventory.</summary>
    public static bool NeedsAction(NotificationKind kind) => kind is NotificationKind.ApprovalRequested or NotificationKind.TeamTask
        or NotificationKind.TaskReturned or NotificationKind.Escalation or NotificationKind.SecurityAlert;
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
    /// <summary>v1.1 (F3): a step would have granted Prod above Read without an approved ProdWrite escalation; stopped.</summary>
    ProdWriteBlocked,
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
