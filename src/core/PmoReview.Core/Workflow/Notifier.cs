using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

public sealed record WorkflowSettings
{
    public TimeSpan OwnerSla { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan SecuritySla { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan IdTeamSla { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan RoleTeamSla { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Stage 6: how long to wait for the user's first sign-in before a reminder.</summary>
    public TimeSpan VerifySla { get; init; } = TimeSpan.FromHours(72);
    /// <summary>"Shortest lifetime": a recorded TAP may not live longer than this.</summary>
    public int TapMaxLifetimeMinutes { get; init; } = 60;
    /// <summary>Auto-fix retries per idempotency key (request id + step).</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>Local development only: accept team reports without the Entra read (no directory reader configured).</summary>
    public bool AllowUnverifiedEntra { get; init; }
    /// <summary>Base URL of the app, for links in notifications. Optional.</summary>
    public string? AppUrl { get; init; }
    /// <summary>v2.1: after both approvals the agent creates the user, group and membership (worker with the Entra writer).</summary>
    public bool AgentProvisioning { get; init; }
    /// <summary>v2.1: after both approvals the agent assigns the mapping row's Azure role to the group (worker with the role writer).</summary>
    public bool AgentRoleAssignment { get; init; }
    /// <summary>Azure role definition GUIDs the agent may assign (defence in depth; Azure enforces the same with a condition).</summary>
    public IReadOnlyList<string> RoleAssignAllowedRoleIds { get; init; } = [];
    /// <summary>Scopes (prefixes, e.g. /subscriptions/…/resourceGroups/rg-x) where the agent may assign roles.</summary>
    public IReadOnlyList<string> RoleAssignScopes { get; init; } = [];
    /// <summary>
    /// How long a refused agent write is retried before the team does it. A managed identity's token is cached for about
    /// 24 hours, so a newly granted permission can take that long to reach the worker (VERIFIED_FACTS F61).
    /// </summary>
    public TimeSpan PermissionWait { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan SlaFor(WaitingOn w) => w switch
    {
        WaitingOn.Owner => OwnerSla,
        WaitingOn.Security => SecuritySla,
        WaitingOn.IdTeam => IdTeamSla,
        WaitingOn.RoleTeam => RoleTeamSla,
        _ => VerifySla,
    };
}

/// <summary>
/// Writes notifications to the outbox. One per idempotency key (request id + step) and kind; repeats are ignored.
/// Everything is masked first. The worker's sender delivers them (pilot: kept in the app as "Recorded").
/// </summary>
public sealed class Notifier(DataStores stores, IClock clock, ILogger<Notifier> logger)
{
    public async Task EnqueueAsync(string projectId, string? requestId, string step, NotificationKind kind, IEnumerable<string?> to, string subject, string body, CancellationToken ct)
    {
        var recipients = to.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim().ToLowerInvariant()).Distinct().ToList();
        var key = Ids.Key(requestId ?? projectId, step);
        var n = new Notification
        {
            Id = Notification.MakeId(key, kind),
            ProjectId = projectId,
            RequestId = requestId,
            Kind = kind,
            IdempotencyKey = key,
            To = recipients,
            Subject = SecretMasker.Apply(subject)!,
            Body = SecretMasker.Apply(body)!,
            CreatedAt = clock.UtcNow,
            Status = NotificationStatus.Pending,
            // No address configured: keep it in the app so the trail still shows it.
            LastError = recipients.Count == 0 ? "NoRecipients" : null,
        };
        try
        {
            await stores.Notifications.CreateAsync(n, ct);
            logger.LogInformation("notify {Kind} request={RequestId} step={Step} recipients={Count}", kind, requestId, step, recipients.Count);
        }
        catch (DocumentConflictException)
        {
            // Same request + step + kind already queued: idempotent.
        }
    }

    public async Task<IReadOnlyList<Notification>> ForRequestAsync(string projectId, string requestId, CancellationToken ct) =>
        (await stores.Notifications.ListAsync(projectId, ct)).Select(n => n.Value)
            .Where(n => n.ProjectId == projectId && n.RequestId == requestId).OrderBy(n => n.CreatedAt).ToList();
}

/// <summary>The issues list. Same request + step + kind counts occurrences instead of duplicating.</summary>
public sealed class IssueLog(DataStores stores, IClock clock, ILogger<IssueLog> logger)
{
    public async Task RecordAsync(string projectId, string? requestId, string step, IssueKind kind, IssueSeverity severity, string summary, string? detail, CancellationToken ct,
        IssueStatus status = IssueStatus.Open)
    {
        var id = Issue.MakeId(requestId, step, kind);
        var now = clock.UtcNow;
        logger.Log(severity == IssueSeverity.High ? LogLevel.Warning : LogLevel.Information,
            "issue {Kind} request={RequestId} step={Step} severity={Severity}", kind, requestId, step, severity);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await stores.Issues.GetAsync(projectId, id, ct);
            try
            {
                if (existing is null)
                {
                    await stores.Issues.CreateAsync(new Issue
                    {
                        Id = id, ProjectId = projectId, RequestId = requestId, Kind = kind, Step = step, Severity = severity,
                        Summary = SecretMasker.Apply(summary)!, Detail = SecretMasker.Apply(detail), Status = status,
                        FirstSeenAt = now, LastSeenAt = now, ResolvedAt = status == IssueStatus.Open ? null : now,
                    }, ct);
                }
                else
                {
                    await stores.Issues.ReplaceAsync(existing.Value with
                    {
                        Occurrences = existing.Value.Occurrences + 1, LastSeenAt = now, Status = status,
                        Severity = severity > existing.Value.Severity ? severity : existing.Value.Severity,
                        Summary = SecretMasker.Apply(summary)!, Detail = SecretMasker.Apply(detail),
                        ResolvedAt = status == IssueStatus.Open ? null : now,
                    }, existing.ETag, ct);
                }
                return;
            }
            catch (Exception e) when (e is DocumentConflictException or ConcurrencyException)
            {
                // Recorded concurrently; re-read and count it.
            }
        }
    }

    /// <summary>Marks open issues of these kinds for the request as resolved (e.g. after a passing verification).</summary>
    public async Task ResolveAsync(string projectId, string requestId, IReadOnlyCollection<IssueKind> kinds, CancellationToken ct)
    {
        foreach (var v in (await stores.Issues.ListAsync(projectId, ct))
                     .Where(i => i.Value.RequestId == requestId && i.Value.Status == IssueStatus.Open && kinds.Contains(i.Value.Kind)))
        {
            try
            {
                await stores.Issues.ReplaceAsync(v.Value with { Status = IssueStatus.Resolved, ResolvedAt = clock.UtcNow }, v.ETag, ct);
            }
            catch (ConcurrencyException)
            {
                // Updated meanwhile; leave it for the next pass.
            }
        }
    }
}
