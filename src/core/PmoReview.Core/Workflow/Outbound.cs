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

/// <summary>Worker: sends pending notifications. Retries at most 3 times, then marks Failed and logs an issue.</summary>
public sealed class NotificationDispatcher(DataStores stores, INotificationSender sender, IssueLog issues, IClock clock, ILogger<NotificationDispatcher> logger, int maxAttempts = 3)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var handled = 0;
        foreach (var project in await stores.Projects.ListAllAsync(ct))
        {
            foreach (var v in (await stores.Notifications.ListAsync(project.Value.ProjectId, ct)).Where(n => n.Value.Status == NotificationStatus.Pending))
            {
                var n = v.Value;
                Notification next;
                if (!sender.Delivers || n.To.Count == 0)
                {
                    next = n with { Status = NotificationStatus.Recorded, SentAt = clock.UtcNow };
                }
                else
                {
                    try
                    {
                        await sender.SendAsync(n, ct);
                        next = n with { Status = NotificationStatus.Sent, SentAt = clock.UtcNow, Attempts = n.Attempts + 1, LastError = null };
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
        }
        return handled;
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
public sealed class WorkflowWorker(WorkflowAgent agent, NotificationDispatcher notifications, AdoSync ado, DailySummaryJob daily)
{
    public async Task<WorkerSummary> RunOnceAsync(string correlationId, CancellationToken ct)
    {
        var advanced = await agent.RunOnceAsync(correlationId, ct);
        var sent = await notifications.RunOnceAsync(ct);
        var items = await ado.RunOnceAsync(ct);
        var summary = await daily.RunOnceAsync(ct);
        return new WorkerSummary(advanced, sent, items, summary);
    }
}
