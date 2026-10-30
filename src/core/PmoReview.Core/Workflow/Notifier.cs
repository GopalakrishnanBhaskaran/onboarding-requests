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
    /// <summary>v1.1 (F3): a Prod escalation waiting for a ProdApprover.</summary>
    public TimeSpan ProdApproverSla { get; init; } = TimeSpan.FromHours(24);
    /// <summary>
    /// Stage 7: how long the final access check may stay pending before the Operators are reminded. The worker checks within
    /// minutes; the user's first sign-in is not waited for (22 Oct 2026).
    /// </summary>
    public TimeSpan VerifySla { get; init; } = TimeSpan.FromHours(2);
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
    /// Agent TAP (<c>AGENT_TAP</c>; Gokul, 22 Oct 2026; off until approved): for a user the agent has just created, it also
    /// issues a one-time Temporary Access Pass and mails it with the initial password and the MFA set-up steps to the
    /// Enterprise ID mail, once. Needs <see cref="AgentProvisioning"/>, a mail sender and the TAP permission.
    /// </summary>
    public bool AgentTap { get; init; }
    /// <summary>Lifetime of an agent-issued TAP in minutes (the joiner reads it from mail; within the tenant's TAP policy maximum).</summary>
    public int AgentTapLifetimeMinutes { get; init; } = 480;
    /// <summary>
    /// Agent privileged rows (<c>AGENT_PRIVILEGED</c>; Gokul, 22 Oct 2026; off until approved): the agent also provisions and
    /// assigns rows flagged privileged, after both approvals, with the same checks and allow-list. Never the roles in
    /// <see cref="Domain.BuiltInRoles.NeverByAgent"/>. Off: privileged rows go to the teams.
    /// </summary>
    public bool AgentPrivileged { get; init; }
    /// <summary>
    /// How long a refused agent write is retried before the team does it. A managed identity's token is cached for about
    /// 24 hours, so a newly granted permission can take that long to reach the worker (VERIFIED_FACTS F61).
    /// </summary>
    public TimeSpan PermissionWait { get; init; } = TimeSpan.FromHours(24);
    /// <summary>
    /// The Entra tenant the AI agent's identity lives in (<c>AGENT_TENANT_ID</c>). Projects bound to another tenant are
    /// never read or changed by the agent (tenant separation, <see cref="Tenants"/>).
    /// </summary>
    public string? AgentTenantId { get; init; }
    /// <summary>Deployment environment (<c>APP_ENVIRONMENT</c>: dev, test, prod). Shown in the app and in test mail subjects.</summary>
    public string AppEnvironment { get; init; } = "pilot";
    /// <summary>True when the worker mails notifications (<c>NOTIFY_SENDER</c> other than Outbox).</summary>
    public bool MailEnabled { get; init; }
    /// <summary>Non-production only: every mail goes to this address instead of the real recipients (listed in the mail).</summary>
    public string? MailRedirectTo { get; init; }
    /// <summary>
    /// The mail sender's limits (<c>MAIL_MAX_PER_MINUTE</c> / <c>MAIL_MAX_PER_HOUR</c>). Default: the ACS Azure-managed domain,
    /// 5 a minute and 10 an hour per subscription, not raisable (F70); a verified custom domain starts at 30 / 100.
    /// </summary>
    public MailLimits MailLimits { get; init; } = MailLimits.AzureManagedDomain;

    // v1.1 (Gokul, 30 Oct 2026). Every switch defaults to off.
    /// <summary>
    /// F5 (<c>AGENT_OFFBOARDING</c>, off until its permissions are approved): the worker executes confirmed offboarding
    /// plans (revoke sessions, delete the TAP, remove memberships and Azure role assignments, disable the account).
    /// Off: plans are still made and shown; execution is refused.
    /// </summary>
    public bool AgentOffboarding { get; init; }
    /// <summary>F5 (<c>OFFBOARD_DELETE_USER</c>, default false): after disabling, soft-delete the account (restorable 30 days, F75).</summary>
    public bool OffboardDeleteUser { get; init; }
    /// <summary>F2 (<c>TAP_MAIL_SENDER</c>): the one shared mailbox the TAP mail is sent from (Graph sendMail, F81). Null = not configured.</summary>
    public string? TapMailSender { get; init; }
    /// <summary>F4 (<c>MAX_UPLOAD_BYTES</c>): largest accepted upload file. Default 5 MB, never above 25 MB (SWA request limit 30 MB, F88).</summary>
    public int MaxUploadBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>F4 (<c>EXTERNAL_UPLOAD_API_ENABLED</c>, default false): the app-only upload API answers; off it returns 404.</summary>
    public bool ExternalUploadApiEnabled { get; init; }
    /// <summary>F4 external API: the Entra application (client) id of this API (token audience) and the tenant it accepts.</summary>
    public string? ExternalUploadAudience { get; init; }

    public TimeSpan SlaFor(WaitingOn w) => w switch
    {
        WaitingOn.Owner => OwnerSla,
        WaitingOn.Security => SecuritySla,
        WaitingOn.IdTeam => IdTeamSla,
        WaitingOn.RoleTeam => RoleTeamSla,
        WaitingOn.ProdApprover => ProdApproverSla,
        _ => VerifySla,
    };
}

/// <summary>
/// Writes notifications to the outbox. One per idempotency key (request id + step) and kind; repeats are ignored.
/// Everything is masked first. The worker's sender delivers them (pilot: kept in the app as "Recorded").
/// </summary>
public sealed class Notifier(DataStores stores, IClock clock, ILogger<Notifier> logger, IssueLog? issues = null, bool mailEnabled = false)
{
    /// <summary>
    /// Plain mail addresses only (<see cref="MailAddresses.Normalize"/>: guest sign-in forms become the mailbox), each once.
    /// Anything else is left out and listed as skipped: one bad entry must not stop the mail for everyone else.
    /// </summary>
    public static (List<string> To, List<string> Skipped) Addresses(IEnumerable<string?> to)
    {
        var valid = new List<string>();
        var skipped = new List<string>();
        foreach (var t in to.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (MailAddresses.Normalize(t) is { } a) { if (!valid.Contains(a)) valid.Add(a); }
            else if (SecretMasker.Apply(t!.Trim())! is var shown && !skipped.Contains(shown)) skipped.Add(shown);
        }
        return (valid, skipped);
    }

    public async Task EnqueueAsync(string projectId, string? requestId, string step, NotificationKind kind, IEnumerable<string?> to, string subject, string body, CancellationToken ct)
    {
        var (recipients, skipped) = Addresses(to);
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
            Skipped = skipped.Count == 0 ? null : skipped,
        };
        try
        {
            await stores.Notifications.CreateAsync(n, ct);
            logger.LogInformation("notify {Kind} request={RequestId} step={Step} recipients={Count} skipped={Skipped}", kind, requestId, step, recipients.Count, skipped.Count);
        }
        catch (DocumentConflictException)
        {
            return; // Same request + step + kind already queued: idempotent.
        }
        // A request for a decision or an action that reaches nobody is a gap in the contact inventory: say so, never silently.
        if (recipients.Count == 0 && mailEnabled && issues is not null && Notification.NeedsAction(kind))
            await issues.RecordAsync(projectId, requestId, $"no-recipients:{kind}", IssueKind.NeedsDeveloper, IssueSeverity.High,
                $"The {kind} mail reached nobody: no mail address for it. PMO: fill in the team on the Contact inventory page; the mail stays in the app.",
                skipped.Count == 0 ? null : $"Not mail addresses: {string.Join("; ", skipped)}", ct);
    }

    /// <summary>
    /// Records a mail that was already sent directly (the agent's sign-in details). <paramref name="maskedBody"/> is what
    /// the app keeps: built with <c>******</c> in place of the password and the pass, and masked again here.
    /// </summary>
    public async Task RecordSentAsync(string projectId, string requestId, string step, NotificationKind kind, IEnumerable<string?> to, string subject, string maskedBody,
        IReadOnlyList<string> deliveredTo, CancellationToken ct)
    {
        var key = Ids.Key(requestId, step);
        var n = new Notification
        {
            Id = Notification.MakeId(key, kind),
            ProjectId = projectId,
            RequestId = requestId,
            Kind = kind,
            IdempotencyKey = key,
            To = Addresses(to).To,
            Subject = SecretMasker.Apply(subject)!,
            Body = SecretMasker.Apply(maskedBody)!,
            CreatedAt = clock.UtcNow,
            Status = NotificationStatus.Sent,
            SentAt = clock.UtcNow,
            Attempts = 1,
            DeliveredTo = deliveredTo,
        };
        try
        {
            await stores.Notifications.CreateAsync(n, ct);
            logger.LogInformation("notify {Kind} request={RequestId} step={Step} sent directly", kind, requestId, step);
        }
        catch (DocumentConflictException)
        {
            // Already recorded.
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

    /// <summary>Marks one open issue (request or project level, by step and kind) as resolved, e.g. once the mail queue is clear.</summary>
    public async Task ResolveStepAsync(string projectId, string? requestId, string step, IssueKind kind, CancellationToken ct)
    {
        var v = await stores.Issues.GetAsync(projectId, Issue.MakeId(requestId, step, kind), ct);
        if (v is null || v.Value.Status != IssueStatus.Open) return;
        try
        {
            await stores.Issues.ReplaceAsync(v.Value with { Status = IssueStatus.Resolved, ResolvedAt = clock.UtcNow }, v.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            // Updated meanwhile; the next pass looks again.
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
