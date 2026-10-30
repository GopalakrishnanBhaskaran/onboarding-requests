using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <summary>Delivery failure, recorded as a category (no details).</summary>
public sealed class OutboundException(string category, Exception? inner = null) : Exception(category, inner)
{
    public string Category { get; } = category;
}

/// <summary>
/// How many mails the sender may send (F70). Azure Communication Services counts mails per subscription: an Azure-managed
/// domain 5 a minute and 10 an hour (not raisable), a verified custom domain 30 and 100 to start (raisable on request).
/// </summary>
public sealed record MailLimits(int PerMinute, int PerHour)
{
    /// <summary>Recipients one mail may have (ACS: 50); a longer list goes out as several mails, each counted.</summary>
    public const int RecipientsPerMail = 50;

    public static MailLimits AzureManagedDomain => new(5, 10);
    public static MailLimits None => new(int.MaxValue, int.MaxValue);
    public bool Unlimited => PerMinute == int.MaxValue && PerHour == int.MaxValue;

    /// <summary>How many mails a notification to <paramref name="recipients"/> addresses takes.</summary>
    public static int Mails(int recipients) => Math.Max(1, (recipients + RecipientsPerMail - 1) / RecipientsPerMail);
}

/// <summary>
/// The room left under <see cref="MailLimits"/>, counted from the outbox itself: every mail that left the app (queued or
/// sent directly) is a notification with status Sent and its time. Read before sending, so mails wait in the queue
/// instead of being refused by the service.
/// </summary>
public static class MailBudget
{
    /// <param name="reserve">Mails per hour kept free for the agent's sign-in details (sent directly, never queued).</param>
    public static async Task<int> RoomAsync(DataStores stores, IClock clock, MailLimits limits, int reserve, CancellationToken ct)
    {
        if (limits.Unlimited) return int.MaxValue;
        var all = new List<Notification>();
        foreach (var p in await stores.Projects.ListAllAsync(ct))
            all.AddRange((await stores.Notifications.ListAsync(p.Value.ProjectId, ct)).Select(n => n.Value));
        return Room(all, clock.UtcNow, limits, reserve);
    }

    /// <summary>The same, over notifications already read (the dispatcher reads them once per run).</summary>
    public static int Room(IEnumerable<Notification> all, DateTimeOffset now, MailLimits limits, int reserve)
    {
        if (limits.Unlimited) return int.MaxValue;
        int minute = 0, hour = 0;
        foreach (var n in all)
        {
            if (n.Status != NotificationStatus.Sent || n.SentAt is not { } at) continue;
            var mails = MailLimits.Mails(n.DeliveredTo?.Count ?? n.To.Count);
            if (now - at < TimeSpan.FromHours(1)) hour += mails;
            if (now - at < TimeSpan.FromMinutes(1)) minute += mails;
        }
        // The reserve never takes more than a fifth of the hour, so queued mails always move.
        return Math.Max(0, Math.Min(limits.PerMinute - minute, limits.PerHour - Math.Min(reserve, limits.PerHour / 5) - hour));
    }
}

/// <summary>Delivers a notification. Implementations never log or alter the body.</summary>
public interface INotificationSender
{
    string Name { get; }
    /// <summary>False for the pilot sender: notifications are kept in the app (status Recorded), nothing is mailed.</summary>
    bool Delivers { get; }
    Task SendAsync(Notification notification, CancellationToken ct);
}

/// <summary>ACTIVE in the pilot: nothing leaves the app; every notification stays visible on the request page.</summary>
public sealed class OutboxOnlySender : INotificationSender
{
    public string Name => "Outbox";
    public bool Delivers => false;
    public Task SendAsync(Notification notification, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Sends one mail now instead of through the outbox, for content that must never be stored: the agent's sign-in details
/// (initial password + TAP). Same sender and test redirect as <see cref="NotificationDispatcher"/>; the caller records a
/// masked copy (<see cref="Notifier.RecordSentAsync"/>). Not retried: a failure goes to the ID team.
/// </summary>
public interface IDirectMail
{
    /// <summary>False when nothing would be mailed (Outbox only): the agent then never issues a TAP itself.</summary>
    bool Delivers { get; }
    /// <returns>The addresses it was delivered to (the test address when redirected).</returns>
    Task<IReadOnlyList<string>> SendNowAsync(Notification notification, CancellationToken ct);
}

/// <summary>The worker's <see cref="IDirectMail"/>: the configured sender, with the test-environment redirect.</summary>
public sealed class DirectMail(INotificationSender sender, string? redirectTo = null, string environment = "pilot") : IDirectMail
{
    public bool Delivers => sender.Delivers;

    public async Task<IReadOnlyList<string>> SendNowAsync(Notification notification, CancellationToken ct)
    {
        if (!sender.Delivers) throw new OutboundException("NotDelivered");
        var outgoing = NotificationDispatcher.Redirect(notification, redirectTo, environment);
        if (outgoing.To.Count == 0) throw new OutboundException("NoRecipients");
        await sender.SendAsync(outgoing, ct);
        return outgoing.To;
    }
}

/// <summary>
/// Worker: sends pending notifications, most urgent first (<see cref="Notification.Priority"/>), within the sender's
/// <see cref="MailLimits"/>. A mail held back by the limit, or refused by the service as too many (MailThrottled), stays
/// Pending without using an attempt and goes out on a later run; an hour of backlog raises a Warning issue. Other
/// failures are retried at most 3 times, then Failed with an issue. In a test environment with
/// <paramref name="redirectTo"/> every mail goes to that one address, marked as a test and naming the intended
/// recipients; the notification keeps its real recipients and records where it was delivered.
/// </summary>
public sealed class NotificationDispatcher(DataStores stores, INotificationSender sender, IssueLog issues, IClock clock, ILogger<NotificationDispatcher> logger,
    int maxAttempts = 3, string? redirectTo = null, string environment = "pilot", MailLimits? limits = null, int reserve = 0)
{
    /// <summary>The issue step for a mail backlog (project level).</summary>
    public const string BacklogStep = "mail-queue";

    /// <summary>The mail as it leaves the app: unchanged, or redirected to the test address.</summary>
    public Notification Outgoing(Notification n) => Redirect(n, redirectTo, environment);

    /// <summary>Test environments: every mail goes to <paramref name="redirectTo"/>, marked as a test and naming the intended recipients.</summary>
    public static Notification Redirect(Notification n, string? redirectTo, string environment) => redirectTo is null ? n : n with
    {
        To = [redirectTo],
        Subject = $"[{environment.ToUpperInvariant()} test] {n.Subject}",
        Body = $"Test environment ({environment}): this mail was meant for {string.Join(", ", n.To)}. It was redirected to the test address.\n\n{n.Body}",
    };

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var handled = 0;
        var all = new List<Versioned<Notification>>();
        var projectIds = new List<string>();
        foreach (var project in await stores.Projects.ListAllAsync(ct))
        {
            projectIds.Add(project.Value.ProjectId);
            all.AddRange(await stores.Notifications.ListAsync(project.Value.ProjectId, ct));
        }
        var pending = all.Where(n => n.Value.Status == NotificationStatus.Pending).ToList();
        var room = sender.Delivers ? MailBudget.Room(all.Select(n => n.Value), clock.UtcNow, limits ?? MailLimits.None, reserve) : 0;
        var throttled = false;
        var waiting = new List<Notification>();
        foreach (var v in pending.OrderBy(n => Notification.Priority(n.Value.Kind)).ThenBy(n => n.Value.CreatedAt))
        {
            var n = v.Value;
            Notification next;
            if (!sender.Delivers || n.To.Count == 0)
            {
                next = n with { Status = NotificationStatus.Recorded, SentAt = clock.UtcNow };
            }
            else if (throttled || room < MailLimits.Mails(Outgoing(n).To.Count))
            {
                waiting.Add(n); // the mail limit: it waits for a later run, no attempt used
                continue;
            }
            else
            {
                try
                {
                    var outgoing = Outgoing(n);
                    await sender.SendAsync(outgoing, ct);
                    room -= MailLimits.Mails(outgoing.To.Count);
                    next = n with { Status = NotificationStatus.Sent, SentAt = clock.UtcNow, Attempts = n.Attempts + 1, LastError = null, DeliveredTo = outgoing.To };
                }
                catch (OutboundException e) when (e.Category == "MailThrottled")
                {
                    // The service says too many: stop sending this run; this and the rest wait (no attempt used).
                    throttled = true;
                    waiting.Add(n);
                    logger.LogWarning("notify throttled kind={Kind} request={RequestId}: waiting for the next run", n.Kind, n.RequestId);
                    if (n.LastError == "MailThrottled") continue;
                    next = n with { LastError = "MailThrottled" };
                }
                catch (OutboundException e)
                {
                    var attempts = n.Attempts + 1;
                    next = n with { Attempts = attempts, LastError = e.Category, Status = attempts >= maxAttempts ? NotificationStatus.Failed : NotificationStatus.Pending };
                    logger.LogWarning("notify send failed {Category} kind={Kind} request={RequestId} attempt={Attempt}", e.Category, n.Kind, n.RequestId, attempts);
                    if (attempts >= maxAttempts)
                        await issues.RecordAsync(n.ProjectId, n.RequestId, $"notify:{n.Id}", IssueKind.NeedsDeveloper, IssueSeverity.Warning,
                            $"{n.Kind} notification could not be sent after {maxAttempts} attempts ({e.Category}).", null, ct);
                }
            }
            try
            {
                await stores.Notifications.ReplaceAsync(next, v.ETag, ct);
                handled++;
            }
            catch (ConcurrencyException)
            {
                // Another worker instance handled it.
            }
        }
        if (sender.Delivers) await BacklogAsync(projectIds, waiting, ct);
        return handled;
    }

    /// <summary>Mails held back for over an hour: one Warning issue per project (resolved once its queue is clear).</summary>
    private async Task BacklogAsync(IEnumerable<string> projects, List<Notification> waiting, CancellationToken ct)
    {
        var l = limits ?? MailLimits.None;
        foreach (var projectId in projects)
        {
            var mine = waiting.Where(n => n.ProjectId == projectId).ToList();
            if (mine.Count > 0 && clock.UtcNow - mine.Min(n => n.CreatedAt) > TimeSpan.FromHours(1))
                await issues.RecordAsync(projectId, null, BacklogStep, IssueKind.DependencyFailure, IssueSeverity.Warning,
                    $"{mine.Count} mail(s) waiting for over an hour: the mail limit ({l.PerHour} an hour, {l.PerMinute} a minute) is reached. " +
                    "They go out in order (approvals and team tasks first) and nothing is lost. A verified custom mail domain raises the limit (F70).", null, ct);
            else if (mine.Count == 0)
                await issues.ResolveStepAsync(projectId, null, BacklogStep, IssueKind.DependencyFailure, ct);
        }
    }
}

/// <summary>What the Azure DevOps work item should say. Description = the full masked trail.</summary>
public sealed record WorkItemDraft(string RequestId, string ProjectId, string Title, string Description, IReadOnlyList<string> NewHistory, bool Close, string Tag);

/// <summary>Azure DevOps work items linked to the request id. Idempotent: finds an existing item by tag before creating.</summary>
public interface IWorkItemTracker
{
    bool Enabled { get; }
    Task<AdoLink> SyncAsync(WorkItemDraft draft, AdoLink? existing, CancellationToken ct);
}

/// <summary>Pilot default: no Azure DevOps organisation configured; the status line shows no ADO link.</summary>
public sealed class DisabledWorkItemTracker : IWorkItemTracker
{
    public bool Enabled => false;
    public Task<AdoLink> SyncAsync(WorkItemDraft draft, AdoLink? existing, CancellationToken ct) => Task.FromResult(existing ?? new AdoLink());
}

/// <summary>Worker: keeps one Azure DevOps work item per request in step with the trail (review, approvals, team actions, justification).</summary>
public sealed class AdoSync(DataStores stores, IWorkItemTracker tracker, IssueLog issues, IClock clock, ILogger<AdoSync> logger, int maxAttempts = 3)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!tracker.Enabled) return 0;
        var synced = 0;
        foreach (var project in await stores.Projects.ListAllAsync(ct))
        {
            var projectId = project.Value.ProjectId;
            foreach (var v in await stores.Requests.ListAsync(projectId, ct))
            {
                var r = v.Value;
                var flow = r.Flow;
                if (flow is null) continue;
                var ado = flow.Ado ?? new AdoLink();
                var close = flow.Status is RequestStatus.Completed or RequestStatus.Rejected;
                if (ado.SyncedEvents >= flow.Events.Count && (!close || ado.Closed)) continue;
                var key = Ids.Key(r.Id, "ado");
                if (flow.AttemptsFor(key) >= maxAttempts) continue;

                var runs = (await stores.Runs.ListAsync(projectId, ct)).Select(x => x.Value).Where(x => x.RequestId == r.Id).ToList();
                var notes = (await stores.Notifications.ListAsync(projectId, ct)).Select(x => x.Value).Where(x => x.RequestId == r.Id).ToList();
                var issueList = (await stores.Issues.ListAsync(projectId, ct)).Select(x => x.Value).Where(x => x.RequestId == r.Id).ToList();
                var draft = new WorkItemDraft(r.Id, projectId,
                    $"Onboarding {Messages.Short(r)}: {r.DomainId} — {r.AccessLevel} on {r.Resource} [{StatusText.Label(flow.Status)}]",
                    Reports.Trail(r, runs, notes, issueList, clock.UtcNow),
                    flow.Events.Skip(ado.SyncedEvents).Select(e => SecretMasker.Apply($"{Messages.Time(e.At)} [{e.Actor}] {e.Step}: {e.Note}")!).ToList(),
                    close, $"pmo-request-{r.Id}");
                WorkflowState next;
                try
                {
                    var link = await tracker.SyncAsync(draft, flow.Ado, ct);
                    next = flow with { Ado = link with { SyncedEvents = flow.Events.Count, SyncedAt = clock.UtcNow, Closed = close, Error = null } };
                    synced++;
                }
                catch (OutboundException e)
                {
                    next = flow.WithAttempt(key) with { Ado = ado with { Error = e.Category } };
                    logger.LogWarning("ado sync failed {Category} request={RequestId}", e.Category, r.Id);
                    if (flow.AttemptsFor(key) + 1 >= maxAttempts)
                        await issues.RecordAsync(projectId, r.Id, "ado", IssueKind.NeedsDeveloper, IssueSeverity.Warning,
                            $"Azure DevOps work item could not be synced after {maxAttempts} attempts ({e.Category}).", null, ct);
                }
                try
                {
                    await stores.Requests.ReplaceAsync(r with { Flow = next }, v.ETag, ct);
                }
                catch (ConcurrencyException)
                {
                    // The request moved on meanwhile; the next run syncs the newer events.
                }
            }
        }
        return synced;
    }
}

/// <summary>
/// Optional AI analysis for reports (daily summary, developer issue reports, E2E log analysis). Input is masked; the
/// model analyses and recommends only. Off in the pilot (null).
/// </summary>
public interface IAiAssistant
{
    Task<string?> AnalyseAsync(string purpose, string facts, CancellationToken ct);
}

/// <summary>Worker: the daily summary of the previous UTC day, once per project (idempotent by date).</summary>
public sealed class DailySummaryJob(DataStores stores, Notifier notifier, IClock clock, int sendHourUtc = 6, IAiAssistant? assistant = null)
{
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (now.UtcDateTime.Hour < sendHourUtc) return false;
        var date = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        var sent = false;
        foreach (var project in await stores.Projects.ListAllAsync(ct))
        {
            var p = project.Value;
            var step = $"daily:{date:yyyy-MM-dd}";
            var id = Notification.MakeId(Ids.Key(p.ProjectId, step), NotificationKind.DailySummary);
            if (await stores.Notifications.GetAsync(p.ProjectId, id, ct) is not null) continue;
            var requests = (await stores.Requests.ListAsync(p.ProjectId, ct)).Select(r => r.Value).ToList();
            var issueList = (await stores.Issues.ListAsync(p.ProjectId, ct)).Select(i => i.Value).ToList();
            var summary = Reports.Daily(p.ProjectId, date, requests, issueList, now);
            var analysis = assistant is null ? null : await assistant.AnalyseAsync("daily-summary", summary.Text, ct);
            await notifier.EnqueueAsync(p.ProjectId, null, step, NotificationKind.DailySummary,
                Recipients.Operators(p).Concat(Recipients.Pmo(p)).Concat(Recipients.Security(p)), $"Daily summary {p.Name} — {date:yyyy-MM-dd}",
                analysis is null ? summary.Text : $"{summary.Text}\nAI analysis:\n{analysis}", ct);
            sent = true;
        }
        return sent;
    }
}

public sealed record WorkerSummary(int Advanced, int Notifications, int WorkItems, bool DailySummary);

/// <summary>One worker cycle: agent steps for open requests, notifications, Azure DevOps, daily summary.</summary>
public sealed class WorkflowWorker(WorkflowAgent agent, NotificationDispatcher notifications, AdoSync ado, DailySummaryJob daily, GroupResolutionJob? groups = null)
{
    public async Task<WorkerSummary> RunOnceAsync(string correlationId, CancellationToken ct)
    {
        // v1.1 (F1): group names resolved in Entra first, so the selector and the escalations are fresh.
        if (groups is not null) await groups.RunOnceAsync(ct);
        var advanced = await agent.RunOnceAsync(correlationId, ct);
        var sent = await notifications.RunOnceAsync(ct);
        var items = await ado.RunOnceAsync(ct);
        var summary = await daily.RunOnceAsync(ct);
        return new WorkerSummary(advanced, sent, items, summary);
    }
}
