using System.Text.Json.Serialization;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Handoff;

/// <summary>
/// Where the request stands in Microsoft Entra. This app never approves or assigns:
/// Entra entitlement management does both. These values mirror what Entra reports.
/// </summary>
public enum HandoffStatus
{
    /// <summary>Accepted review, but something stops the hand-off (see <see cref="EntraHandoff.Reason"/>).</summary>
    NotEligible,
    /// <summary>Link issued; the new joiner has not requested the access package yet.</summary>
    AwaitingJoinerRequest,
    /// <summary>Request is in Entra waiting for the approver.</summary>
    PendingApproval,
    /// <summary>Approved; Entra is adding the access (or it is scheduled for the start date).</summary>
    Delivering,
    /// <summary>Entra reports the assignment as delivered.</summary>
    Delivered,
    Denied,
    Cancelled,
    /// <summary>Entra could not deliver the assignment.</summary>
    Failed,
    /// <summary>The assignment reached its end date and Entra removed it.</summary>
    Expired,
}

public sealed record HandoffEvent(DateTimeOffset At, HandoffStatus Status, string Note);

/// <summary>
/// One per accepted, ready review of a request revision. Created by the API when a Reviewer records
/// "Accepted"; afterwards only the background sync writes it (ETag on every write).
/// </summary>
public sealed record EntraHandoff : IDocument
{
    /// <summary>"{requestId}.r{revision}"</summary>
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string RequestId { get; init; }
    public required int RequestRevision { get; init; }
    public required string ReviewRunId { get; init; }
    public required string AcceptedBy { get; init; }
    public required HandoffStatus Status { get; init; }
    /// <summary>Why the hand-off is not possible (NotEligible only).</summary>
    public string? Reason { get; init; }
    /// <summary>From the reviewed snapshot, not the live request.</summary>
    public string? JoinerUpn { get; init; }
    public string? MappingId { get; init; }
    public int? MappingVersion { get; init; }
    public string? AccessPackageId { get; init; }
    public string? MyAccessLink { get; init; }
    public DateOnly? RequestedStart { get; init; }
    public DateOnly? RequestedEnd { get; init; }
    public string? EntraRequestId { get; init; }
    public string? EntraRequestState { get; init; }
    public string? EntraRequestType { get; init; }
    public string? EntraAssignmentId { get; init; }
    public string? EntraAssignmentState { get; init; }
    public DateTimeOffset? AccessStart { get; init; }
    public DateTimeOffset? AccessEnd { get; init; }
    /// <summary>Entra's assignment ends later than the reviewed end date, or never ends.</summary>
    public bool EndDateExceedsRequest { get; init; }
    /// <summary>Joiner already held the access package before the hand-off.</summary>
    public bool PreExistingAssignment { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    /// <summary>Error category of the last failed sync (no details), cleared on success.</summary>
    public string? LastSyncError { get; init; }
    public IReadOnlyList<HandoffEvent> History { get; init; } = [];
    public int SchemaVersion { get; init; } = 1;
    [JsonIgnore] public string PartitionKey => ProjectId;

    public static string MakeId(string requestId, int revision) => $"{requestId}.r{revision}";

    /// <summary>No further sync. Denied/Cancelled stay open so a new Entra request is still picked up.</summary>
    [JsonIgnore] public bool IsClosed => Status is HandoffStatus.NotEligible or HandoffStatus.Expired;
}
