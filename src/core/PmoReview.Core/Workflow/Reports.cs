using System.Globalization;
using System.Text;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>Always-visible status: Request ID | Requester | Current stage | Waiting on | Done | Next | Time in stage | ADO link.</summary>
public sealed record StatusLine(
    string RequestId,
    string ProjectId,
    string Requester,
    RequestStatus Status,
    string CurrentStage,
    WaitingOn WaitingOn,
    string WaitingOnLabel,
    IReadOnlyList<string> Done,
    string Next,
    long TimeInStageSeconds,
    string TimeInStage,
    string? AdoLink,
    string? DomainId,
    string? Resource,
    string? AccessLevel,
    DateTimeOffset UpdatedAt);

public sealed record DurationItem(string Name, long Seconds, string Text);

/// <summary>Time per stage (including wait time per team) and total.</summary>
public sealed record Timings(IReadOnlyList<DurationItem> PerStage, IReadOnlyList<DurationItem> PerTeam, DurationItem Total);

public sealed record CountItem(string Name, int Count);

public sealed record DailySummary(
    string ProjectId,
    DateOnly Date,
    int Submitted,
    int Completed,
    int Rejected,
    int Returned,
    int FailedNeedsDev,
    IReadOnlyList<CountItem> OpenByStatus,
    IReadOnlyList<CountItem> WaitingOnCounts,
    IReadOnlyList<DurationItem> WaitTimePerTeam,
    IReadOnlyList<DurationItem> AverageTimePerStageCompleted,
    int SlaBreaches,
    IReadOnlyList<CountItem> OpenIssuesByKind,
    string Text);

public sealed record DeveloperItem(string RequestId, RequestStatus Status, string? Reason, IReadOnlyDictionary<string, int> Attempts, IReadOnlyList<WorkflowEvent> LastEvents, string Report);

public sealed record DeveloperReport(string ProjectId, DateTimeOffset GeneratedAt, IReadOnlyList<DeveloperItem> Requests, IReadOnlyList<Issue> OpenIssues, string Text);

/// <summary>Status tracking, timings, the E2E trail and the reports. Pure functions over stored data.</summary>
public static class Reports
{
    private static readonly string[] Steps = ["Submitted", "AI review", "Owner approval", "Security approval", "ID team", "Role assignment", "Verification"];

    public static StatusLine StatusLine(AccessRequest r, DateTimeOffset now)
    {
        var flow = r.Flow;
        if (flow is null)
            return new StatusLine(r.Id, r.ProjectId, r.CreatedByName ?? r.CreatedBy, RequestStatus.Completed, "Legacy (v1)", WaitingOn.None, "—", [], "—", 0, "—", null,
                r.DomainId, r.Resource, r.AccessLevel, r.UpdatedAt);
        var inStage = flow.IsOpen || flow.Status == RequestStatus.Returned ? now - flow.StageSince : TimeSpan.Zero;
        return new StatusLine(r.Id, r.ProjectId, r.CreatedByName ?? r.CreatedBy, flow.Status, StatusText.Label(flow.Status), flow.WaitingOn,
            StatusText.Label(flow.WaitingOn), Done(flow), Next(flow), (long)inStage.TotalSeconds, Span(inStage), flow.Ado?.Url,
            r.DomainId, r.Resource, r.AccessLevel, r.UpdatedAt);
    }

    private static IReadOnlyList<string> Done(WorkflowState f)
    {
        var reached = f.Status switch
        {
            RequestStatus.Submitted or RequestStatus.AiReview => 1,
            RequestStatus.Returned => 1,
            RequestStatus.AwaitingOwner => 2,
            RequestStatus.AwaitingSecurity => 3,
            RequestStatus.WithIdTeam => 4,
            RequestStatus.WithRoleTeam => 5,
            RequestStatus.Verifying => 6,
            RequestStatus.Completed => 7,
            RequestStatus.Rejected => f.CurrentApproval(ApprovalStage.Owner)?.Decision == ApprovalDecision.Approved ? 3 : 2,
            _ => f.Events.Any(e => e.Step == "role-team-verified") ? 6 : f.Events.Any(e => e.Step == "id-team-verified") ? 5
                : f.CurrentApproval(ApprovalStage.Security) is not null ? 4 : f.Scope is not null ? 2 : 1,
        };
        return Steps.Take(reached).ToList();
    }

    private static string Next(WorkflowState f) => f.Status switch
    {
        RequestStatus.Submitted or RequestStatus.AiReview => "AI review against the mapping sheet and response document",
        RequestStatus.Returned => "Requester corrects the request (or PMO the mapping sheet) and submits again",
        RequestStatus.AwaitingOwner => "Named Owner approves or rejects the business need",
        RequestStatus.AwaitingSecurity => "Security approves or rejects risk and access level",
        RequestStatus.WithIdTeam => f.IdTeamTask?.State == TeamTaskState.Reported ? "AI verifies the ID team's work" : "ID team creates user/group, adds membership, issues TAP",
        RequestStatus.WithRoleTeam => f.RoleTeamTask?.State == TeamTaskState.Reported ? "AI verifies the role assignment" : "Role assignment team assigns the role",
        RequestStatus.Verifying => "User signs in and registers a method; AI verifies; TAP removed",
        RequestStatus.FailedNeedsDev => "Developer investigates (see developer report)",
        _ => "—",
    };

    public static Timings Timings(AccessRequest r, DateTimeOffset now)
    {
        var f = r.Flow;
        if (f is null || f.Stages.Count == 0) return new Timings([], [], Item("Total", TimeSpan.Zero));
        var end = f.CompletedAt ?? (f.IsOpen || f.Status == RequestStatus.Returned ? now : f.Stages[^1].EnteredAt);
        var perStage = new Dictionary<string, TimeSpan>();
        var perTeam = new Dictionary<string, TimeSpan>();
        foreach (var s in f.Stages)
        {
            var d = (s.LeftAt ?? end) - s.EnteredAt;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            var stage = StatusText.Label(s.Status);
            perStage[stage] = perStage.GetValueOrDefault(stage) + d;
            if (s.WaitingOn != WaitingOn.None)
            {
                var team = StatusText.Label(s.WaitingOn);
                perTeam[team] = perTeam.GetValueOrDefault(team) + d;
            }
        }
        return new Timings(
            perStage.Select(p => Item(p.Key, p.Value)).ToList(),
            perTeam.OrderByDescending(p => p.Value).Select(p => Item(p.Key, p.Value)).ToList(),
            Item("Total", end - f.Stages[0].EnteredAt));
    }

    public static string TimingText(Timings t) =>
        string.Join("\n", t.PerStage.Select(s => $"- {s.Name}: {s.Text}"))
        + "\nWait time per team:\n" + string.Join("\n", t.PerTeam.Select(s => $"- {s.Name}: {s.Text}"))
        + $"\nTotal: {t.Total.Text}";

    /// <summary>
    /// E2E trail (audit and log analysis): every event, approval, team action, check, notification and issue in time
    /// order, followed by an analysis (time per stage, wait per team, retries, returns). Masked; used for the ADO work item.
    /// </summary>
    public static string Trail(AccessRequest r, IReadOnlyList<ReviewRun> runs, IReadOnlyList<Notification> notifications, IReadOnlyList<Issue> issues, DateTimeOffset now)
    {
        var f = r.Flow;
        var sb = new StringBuilder();
        sb.AppendLine($"Request {r.Id} — {Messages.What(r)}");
        sb.AppendLine($"Requester: {r.CreatedByName ?? r.CreatedBy}; Owner: {r.Owner}; justification: {SecretMasker.Apply(r.Justification)}");
        if (f is null) return sb.AppendLine("Legacy v1 request: no workflow trail.").ToString();
        sb.AppendLine($"Status: {StatusText.Label(f.Status)} (waiting on {StatusText.Label(f.WaitingOn)})");
        if (f.Scope is { } s) sb.AppendLine($"Reviewed: row {s.MappingRowId} v{s.MappingRowVersion} → group {s.EntraGroup}, role {s.Role}{(s.RoleScope is null ? "" : $" at {s.RoleScope}")}; risk flags: {(s.RiskFlags.Count == 0 ? "none" : string.Join(", ", s.RiskFlags))}");

        var lines = new List<(DateTimeOffset At, string Text)>();
        lines.AddRange(f.Events.Select(e => (e.At, $"[{e.Actor}] {e.Step}: {e.Note}")));
        foreach (var run in runs.OrderBy(x => x.QueuedAt))
        {
            var passed = run.Result?.Checks.Count(c => c.Status == CheckStatus.Pass) ?? 0;
            var total = run.Result?.Checks.Count(c => c.Status is CheckStatus.Pass or CheckStatus.Fail or CheckStatus.Pending) ?? 0;
            lines.Add((run.CompletedAt ?? run.QueuedAt, $"[review] run {run.Id} ({run.Trigger}) {run.ProcessingStatus}{(run.Outcome is null ? "" : $" {run.Outcome}")} — {passed}/{total} checks passed{(run.ErrorCategory is null ? "" : $", error {run.ErrorCategory}")}"));
            foreach (var c in run.Result?.Checks.Where(c => c.Status == CheckStatus.Fail) ?? [])
                lines.Add((run.CompletedAt ?? run.QueuedAt, $"    FAIL {c.Code}: {c.Detail}"));
        }
        foreach (var a in f.Approvals)
            lines.Add((a.At, $"[{a.ByName}] {a.Stage} {a.Decision}{(a.Superseded ? " (superseded)" : "")} via {a.Channel}, review {a.ReviewRunId}{(a.Remarks is null ? "" : $": {a.Remarks}")}"));
        foreach (var t in new[] { f.IdTeamTask, f.RoleTeamTask }.OfType<TeamTask>())
        {
            if (t.PickedUpAt is { } p) lines.Add((p, $"[{t.PickedUpBy}] {Messages.Team(t.Team)} picked up the task"));
            if (t.ReportedAt is { } at) lines.Add((at, $"[{t.ReportedBy}] {Messages.Team(t.Team)} reported: {Report(t)}"));
        }
        lines.AddRange(notifications.Select(n => (n.CreatedAt, $"[notify] {n.Kind} to {n.To.Count} recipient(s): {n.Status}")));
        lines.AddRange(issues.Select(i => (i.FirstSeenAt, $"[issue] {i.Kind} ({i.Severity}, {i.Status}, x{i.Occurrences}): {i.Summary}")));

        sb.AppendLine().AppendLine("Trail:");
        foreach (var (at, text) in lines.OrderBy(l => l.At)) sb.AppendLine($"{Messages.Time(at)}  {SecretMasker.Apply(text)}");

        var timings = Timings(r, now);
        sb.AppendLine().AppendLine("Analysis:");
        sb.AppendLine("Time per stage:").AppendLine(TimingText(timings));
        var longest = timings.PerTeam.FirstOrDefault();
        if (longest is not null) sb.AppendLine($"Longest wait: {longest.Name} ({longest.Text}).");
        var retries = f.Events.Count(e => e.Step == "auto-retry");
        var returns = (f.IdTeamTask?.ReturnCount ?? 0) + (f.RoleTeamTask?.ReturnCount ?? 0);
        sb.AppendLine($"Reviews: {runs.Count}; auto-retries: {retries}; returns to teams: {returns}; open issues: {issues.Count(i => i.Status == IssueStatus.Open)}.");
        return sb.ToString();
    }

    private static string Report(TeamTask t) => t.IdReport is { } id
        ? $"user {id.UserPrincipalName}, group {id.GroupName}{(id.GroupCreated ? " (created)" : "")}, membership {(id.AddedToGroup ? "added" : "not confirmed")}, " +
          $"mapping sheet {(id.MappingSheetUpdated ? "updated" : "unchanged")}" +
          (id.Tap is { } tap ? $", TAP issued {Messages.Time(tap.IssuedAt)} expires {Messages.Time(tap.ExpiresAt)} one-time={tap.OneTime} by {tap.IssuedBy}" : ", no TAP")
        : t.RoleReport is { } role ? $"assigned {string.Join(", ", role.AssignedRoles.Select(a => a.Scope is null ? a.Role : $"{a.Role} at {a.Scope}"))}" : "—";

    public static string DeveloperReportFor(AccessRequest r, DateTimeOffset now)
    {
        var f = r.Flow!;
        var sb = new StringBuilder();
        sb.AppendLine($"Developer issue report — request {r.Id} ({r.ProjectId})");
        sb.AppendLine($"Status: {StatusText.Label(f.Status)}; reason: {f.LastReason}");
        sb.AppendLine($"Attempts: {(f.Attempts.Count == 0 ? "none" : string.Join(", ", f.Attempts.Select(a => $"{a.Key}={a.Value}")))}");
        sb.AppendLine("Last events:");
        foreach (var e in f.Events.TakeLast(10)) sb.AppendLine($"  {Messages.Time(e.At)} [{e.Actor}] {e.Step}: {e.Note}");
        sb.AppendLine("Next: check the worker logs for this request id (structured logs carry request id and step), fix the cause, then an Operator retries the review or re-submits.");
        return SecretMasker.Apply(sb.ToString())!;
    }

    public static DeveloperReport Developer(string projectId, IReadOnlyList<AccessRequest> requests, IReadOnlyList<Issue> issues, DateTimeOffset now)
    {
        var items = requests.Where(r => r.Flow?.Status == RequestStatus.FailedNeedsDev)
            .Select(r => new DeveloperItem(r.Id, r.Flow!.Status, r.Flow.LastReason, r.Flow.Attempts, r.Flow.Events.TakeLast(10).ToList(), DeveloperReportFor(r, now)))
            .ToList();
        var open = issues.Where(i => i.Status == IssueStatus.Open && i.Kind is IssueKind.NeedsDeveloper or IssueKind.DependencyFailure).OrderByDescending(i => i.LastSeenAt).ToList();
        var text = $"Developer report {projectId} — {Messages.Time(now)}\nFailed-Needs-Dev: {items.Count}\nOpen developer issues: {open.Count}\n\n"
                   + string.Join("\n", items.Select(i => i.Report))
                   + string.Join("\n", open.Select(i => $"- {i.Kind} request {i.RequestId} step {i.Step} x{i.Occurrences}: {i.Summary}"));
        return new DeveloperReport(projectId, now, items, open, text);
    }

    /// <summary>Daily summary for one UTC day, with wait time per team.</summary>
    public static DailySummary Daily(string projectId, DateOnly date, IReadOnlyList<AccessRequest> requests, IReadOnlyList<Issue> issues, DateTimeOffset now)
    {
        var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var flows = requests.Where(r => r.Flow is not null).ToList();
        bool On(DateTimeOffset t) => t >= from && t < to;
        int Count(string step) => flows.Count(r => r.Flow!.Events.Any(e => e.Step == step && On(e.At)));

        var submitted = flows.Count(r => On(r.CreatedAt));
        var completed = Count("completed");
        var rejected = flows.Count(r => r.Flow!.Approvals.Any(a => a.Decision == ApprovalDecision.Rejected && On(a.At)));
        var returned = Count("review-fail");
        var failed = Count("failed-needs-dev");
        var open = flows.Where(r => r.Flow!.IsOpen || r.Flow.Status == RequestStatus.Returned).ToList();
        var byStatus = open.GroupBy(r => StatusText.Label(r.Flow!.Status)).Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList();
        var waiting = open.GroupBy(r => StatusText.Label(r.Flow!.WaitingOn)).Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList();

        // Wait time per team during the day: stage entries clipped to the day.
        var wait = new Dictionary<string, TimeSpan>();
        var end = now < to ? now : to;
        foreach (var r in flows)
            foreach (var s in r.Flow!.Stages.Where(s => s.WaitingOn != WaitingOn.None))
            {
                var a = s.EnteredAt > from ? s.EnteredAt : from;
                var b = (s.LeftAt ?? end) < end ? s.LeftAt ?? end : end;
                if (b > a) wait[StatusText.Label(s.WaitingOn)] = wait.GetValueOrDefault(StatusText.Label(s.WaitingOn)) + (b - a);
            }
        var waitItems = wait.OrderByDescending(w => w.Value).Select(w => Item(w.Key, w.Value)).ToList();

        var done = flows.Where(r => r.Flow!.CompletedAt is { } c && On(c)).Select(r => Timings(r, now)).ToList();
        var avg = done.SelectMany(t => t.PerStage).GroupBy(s => s.Name)
            .Select(g => Item(g.Key, TimeSpan.FromSeconds(g.Average(s => s.Seconds)))).ToList();
        if (done.Count > 0) avg.Add(Item("Total", TimeSpan.FromSeconds(done.Average(t => t.Total.Seconds))));

        var sla = issues.Count(i => i.Kind is IssueKind.ApprovalSlaBreached or IssueKind.TaskSlaBreached && On(i.LastSeenAt));
        var openIssues = issues.Where(i => i.Status == IssueStatus.Open).GroupBy(i => i.Kind.ToString()).Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList();

        var text = new StringBuilder()
            .AppendLine($"Daily summary {projectId} — {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (UTC)")
            .AppendLine($"Submitted {submitted} · Completed {completed} · Rejected {rejected} · Returned {returned} · Failed-Needs-Dev {failed}")
            .AppendLine($"Open by status: {Join(byStatus)}")
            .AppendLine($"Waiting on: {Join(waiting)}")
            .AppendLine($"Wait time per team today: {string.Join(", ", waitItems.Select(w => $"{w.Name} {w.Text}"))}")
            .AppendLine($"Average time per stage (completed today): {(avg.Count == 0 ? "—" : string.Join(", ", avg.Select(a => $"{a.Name} {a.Text}")))}")
            .AppendLine($"SLA breaches today: {sla} · Open issues: {Join(openIssues)}")
            .ToString();
        return new DailySummary(projectId, date, submitted, completed, rejected, returned, failed, byStatus, waiting, waitItems, avg, sla, openIssues, text);
    }

    private static string Join(IReadOnlyList<CountItem> items) => items.Count == 0 ? "none" : string.Join(", ", items.Select(i => $"{i.Name} {i.Count}"));

    private static DurationItem Item(string name, TimeSpan d) => new(name, (long)d.TotalSeconds, Span(d));

    public static string Span(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{(int)d.TotalMinutes}m";
    }
}
