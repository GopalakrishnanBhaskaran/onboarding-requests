using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Handoff;

public sealed record EntraSubject(string? ObjectId, string? PrincipalName, string? Email, string? DisplayName)
{
    public bool Is(string upn) =>
        string.Equals(PrincipalName, upn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Email, upn, StringComparison.OrdinalIgnoreCase);
}

/// <summary>accessPackageAssignmentRequest (Graph v1.0), the fields this app reads.</summary>
public sealed record EntraRequest(
    string Id, string RequestType, string State, string? Status, DateTimeOffset CreatedAt,
    EntraSubject? Requestor, DateTimeOffset? ScheduleStart, DateTimeOffset? ScheduleEnd);

/// <summary>accessPackageAssignment (Graph v1.0), the fields this app reads.</summary>
public sealed record EntraAssignment(
    string Id, string State, string? Status, EntraSubject? Target,
    DateTimeOffset? ScheduleStart, DateTimeOffset? ScheduleEnd, string? ExpirationType, DateTimeOffset? ExpiredAt);

/// <summary>Read-only view of Entra entitlement management. The implementation holds EntitlementManagement.Read.All only.</summary>
public interface IEntraGovernanceReader
{
    Task<IReadOnlyList<EntraRequest>> ListRequestsAsync(string accessPackageId, CancellationToken ct);
    Task<IReadOnlyList<EntraAssignment>> ListAssignmentsAsync(string accessPackageId, CancellationToken ct);
}

/// <summary>Reader failure the sync records as a category, never as details.</summary>
public sealed class EntraReadException(string category, Exception? inner = null) : Exception(category, inner)
{
    public string Category { get; } = category;
}

public sealed record SyncSummary(int Checked, int Changed, int Errors);

/// <summary>
/// Background sync: reads Entra and records where each open hand-off stands. It is the only writer
/// of hand-off records after creation. It never calls a write API in Entra.
/// </summary>
public sealed class HandoffSync(DataStores stores, IEntraGovernanceReader entra, IClock clock, ILogger<HandoffSync> logger)
{
    /// <summary>A request created a little before the hand-off record still counts (clock skew, quick joiners).</summary>
    public static readonly TimeSpan MatchWindow = TimeSpan.FromMinutes(10);
    /// <summary>Open hand-offs older than this are no longer polled.</summary>
    public static readonly TimeSpan MaxTracking = TimeSpan.FromDays(400);

    public async Task<SyncSummary> RunOnceAsync(CancellationToken ct)
    {
        var open = (await stores.Handoffs.ListAllAsync(ct))
            .Where(h => !h.Value.IsClosed && h.Value.AccessPackageId is not null && h.Value.JoinerUpn is not null
                        && clock.UtcNow - h.Value.CreatedAt < MaxTracking)
            .ToList();

        int changed = 0, errors = 0;
        foreach (var group in open.GroupBy(h => h.Value.AccessPackageId!, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<EntraRequest> requests;
            IReadOnlyList<EntraAssignment> assignments;
            string? error = null;
            try
            {
                requests = await entra.ListRequestsAsync(group.Key, ct);
                assignments = await entra.ListAssignmentsAsync(group.Key, ct);
            }
            catch (EntraReadException e)
            {
                (requests, assignments, error) = ([], [], e.Category);
                logger.LogWarning("handoff sync: Entra read failed {Category} package={PackageId}", e.Category, group.Key);
            }

            foreach (var current in group)
            {
                var next = error is null
                    ? Apply(current.Value, requests, assignments, clock.UtcNow)
                    : current.Value with { LastCheckedAt = clock.UtcNow, LastSyncError = error };
                if (error is not null) errors++;
                if (next.Status != current.Value.Status) changed++;
                try
                {
                    await stores.Handoffs.ReplaceAsync(next, current.ETag, ct);
                }
                catch (ConcurrencyException)
                {
                    // Changed since we read it (e.g. a re-acceptance); the next cycle re-reads.
                }
            }
        }
        logger.LogInformation("handoff sync: checked={Checked} changed={Changed} errors={Errors}", open.Count, changed, errors);
        return new SyncSummary(open.Count, changed, errors);
    }

    /// <summary>Pure: the hand-off as Entra currently reports it.</summary>
    public static EntraHandoff Apply(EntraHandoff h, IReadOnlyList<EntraRequest> requests, IReadOnlyList<EntraAssignment> assignments, DateTimeOffset now)
    {
        var upn = h.JoinerUpn!;
        var since = h.CreatedAt - MatchWindow;
        var request = requests
            .Where(r => r.Requestor?.Is(upn) == true && r.CreatedAt >= since
                        && r.RequestType is "userAdd" or "adminAdd" or "systemAdd")
            .MaxBy(r => r.CreatedAt);
        var mine = assignments.Where(a => a.Target?.Is(upn) == true).ToList();
        var active = mine.Where(a => a.State is "delivered" or "delivering" or "partiallyDelivered").MaxBy(a => a.ScheduleStart ?? DateTimeOffset.MinValue);
        var expired = mine.Where(a => a.State == "expired").MaxBy(a => a.ExpiredAt ?? a.ScheduleEnd ?? DateTimeOffset.MinValue);

        HandoffStatus status;
        EntraAssignment? assignment = null;
        string note;
        if (active is not null)
        {
            assignment = active;
            status = active.State == "delivered" ? HandoffStatus.Delivered : HandoffStatus.Delivering;
            note = status == HandoffStatus.Delivered ? "Entra reports the access as delivered." : "Approved; Entra is delivering the access.";
        }
        else if (expired is not null && (h.Status is HandoffStatus.Delivered or HandoffStatus.Delivering or HandoffStatus.Expired))
        {
            assignment = expired;
            status = HandoffStatus.Expired;
            note = "The access reached its end date and Entra removed it.";
        }
        else if (request is not null)
        {
            (status, note) = request.State switch
            {
                "submitted" or "pendingApproval" => (HandoffStatus.PendingApproval, "The new joiner requested access; waiting for the Entra approver."),
                "delivering" or "scheduled" or "partiallyDelivered" => (HandoffStatus.Delivering, "Approved in Entra; access is being added or is scheduled for the start date."),
                "delivered" => (HandoffStatus.Delivering, "Entra reports the request delivered; waiting for the assignment to show."),
                "denied" => (HandoffStatus.Denied, "The Entra approver denied the request."),
                "canceled" => (HandoffStatus.Cancelled, "The request was cancelled in Entra."),
                "deliveryFailed" => (HandoffStatus.Failed, "Entra could not deliver the access."),
                _ => (h.Status, $"Entra request state '{request.State}' is not recognised; status unchanged."),
            };
        }
        else
        {
            status = h.Status == HandoffStatus.NotEligible ? h.Status : HandoffStatus.AwaitingJoinerRequest;
            note = "No Entra request from the new joiner yet.";
        }

        var preExisting = active is not null && request is null && active.ScheduleStart is { } start && start < since;
        if (preExisting) note = "The new joiner already held this access package before the hand-off.";

        var end = assignment?.ScheduleEnd ?? request?.ScheduleEnd;
        var noExpiry = assignment is not null && string.Equals(assignment.ExpirationType, "noExpiration", StringComparison.OrdinalIgnoreCase);
        var allowedEnd = h.RequestedEnd is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero).AddDays(1) : (DateTimeOffset?)null;
        var exceeds = allowedEnd is not null && (noExpiry || (end is not null && end > allowedEnd));

        var history = h.History;
        if (status != h.Status) history = [.. h.History, new HandoffEvent(now, status, note)];

        return h with
        {
            Status = status,
            EntraRequestId = request?.Id ?? h.EntraRequestId,
            EntraRequestState = request?.State ?? h.EntraRequestState,
            EntraRequestType = request?.RequestType ?? h.EntraRequestType,
            EntraAssignmentId = assignment?.Id ?? h.EntraAssignmentId,
            EntraAssignmentState = assignment?.State ?? h.EntraAssignmentState,
            AccessStart = assignment?.ScheduleStart ?? request?.ScheduleStart ?? h.AccessStart,
            AccessEnd = end ?? h.AccessEnd,
            EndDateExceedsRequest = exceeds,
            PreExistingAssignment = preExisting,
            LastCheckedAt = now,
            LastSyncError = null,
            History = history,
        };
    }
}
