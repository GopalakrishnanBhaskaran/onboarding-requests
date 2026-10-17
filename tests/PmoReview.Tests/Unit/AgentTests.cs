using PmoReview.Adapters;
using PmoReview.Adapters.Review;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>SLA, auto-fix limits, idempotency and failure handling of the agent.</summary>
public class AgentTests
{
    [Fact]
    public async Task Owner_pending_past_sla_gets_one_reminder_then_escalation_to_the_manager_and_never_auto_approval()
    {
        var app = TestApp.Create();
        app.Directory.SetManager("sample-user-olivia@sample.invalid", "sample.olivia.manager@sample.invalid");
        var id = await app.To(RequestStatus.AwaitingOwner);

        app.Clock.Advance(TimeSpan.FromHours(23));
        await app.RunWorker();
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.Reminder);

        app.Clock.Advance(TimeSpan.FromHours(2)); // 25 h > 24 h SLA
        await app.RunWorker();
        await app.RunWorker();
        var reminder = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Reminder);
        Assert.Equal(["sample-user-olivia@sample.invalid"], reminder.To);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.ApprovalSlaBreached && i.Severity == IssueSeverity.Warning);
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.Escalation);

        app.Clock.Advance(TimeSpan.FromHours(24)); // past 2 x SLA
        await app.RunWorker();
        var escalation = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Escalation);
        Assert.Equal(["sample.olivia.manager@sample.invalid"], escalation.To);
        Assert.Contains("Nothing is approved automatically", escalation.Body);

        app.Clock.Advance(TimeSpan.FromDays(10));
        await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.AwaitingOwner, flow.Status);
        Assert.Empty(flow.Approvals);
        Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Escalation);
    }

    [Fact]
    public async Task Security_escalation_goes_to_the_configured_security_manager()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingSecurity);
        app.Clock.Advance(TimeSpan.FromHours(25));
        await app.RunWorker();
        app.Clock.Advance(TimeSpan.FromHours(24));
        await app.RunWorker();
        Assert.Equal(["sample.security.lead.alpha@sample.invalid"], Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Escalation).To);
    }

    [Fact]
    public async Task Owner_without_an_entra_manager_escalates_to_the_fallback_contact()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        app.Clock.Advance(TimeSpan.FromHours(25));
        await app.RunWorker();
        app.Clock.Advance(TimeSpan.FromHours(24));
        await app.RunWorker();
        Assert.Equal(["sample.owner.manager.alpha@sample.invalid"], Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Escalation).To);
    }

    [Fact]
    public async Task Id_team_task_not_picked_up_past_sla_gets_reminder_then_escalation()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        app.Clock.Advance(TimeSpan.FromHours(25));
        await app.RunWorker();
        Assert.Equal(["sample.idteam.alpha@sample.invalid"], Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Reminder).To);
        app.Clock.Advance(TimeSpan.FromHours(24));
        await app.RunWorker();
        Assert.Equal(["sample.idteam.lead.alpha@sample.invalid"], Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.Escalation).To);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.TaskSlaBreached && i.Severity == IssueSeverity.High);
    }

    [Fact]
    public async Task Failed_review_is_retried_at_most_three_times_then_failed_needs_dev_with_a_developer_report()
    {
        var app = TestApp.With(new FakeModel((_, _) => throw new HttpRequestException("model down")));
        var id = await app.SubmitId();
        for (var i = 0; i < 5; i++) await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.FailedNeedsDev, flow.Status);
        Assert.Equal(WaitingOn.Developer, flow.WaitingOn);
        Assert.Equal(3, flow.Attempts.Single().Value);
        Assert.Equal(3, flow.Events.Count(e => e.Step == "auto-retry"));
        var dev = Assert.Single(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper);
        Assert.Contains("Developer issue report", dev.Detail);
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.DeveloperReport && n.To.Contains("sample.operator.alpha@sample.invalid"));
        var runs = (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Count(r => r.Value.RequestId == id);
        Assert.Equal(4, runs); // first run + 3 retries, never more
    }

    [Fact]
    public async Task Entra_outage_is_retried_then_failed_needs_dev()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        app.Directory.AddUser(TestApp.DefaultUser);
        app.Directory.AddMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        await app.ReportIdTeam(id, app.IdReport());
        app.Directory.FailWith = "GraphUnavailable";
        for (var i = 0; i < 3; i++) await app.RunWorker();
        Assert.Equal(RequestStatus.WithIdTeam, await app.Status(id));
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.DependencyFailure);
        await app.RunWorker();
        Assert.Equal(RequestStatus.FailedNeedsDev, await app.Status(id));
    }

    [Fact]
    public async Task Operator_resumes_failed_needs_dev_at_the_failed_stage_after_the_fix()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        app.Directory.AddUser(TestApp.DefaultUser);
        app.Directory.AddMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        await app.ReportIdTeam(id, app.IdReport());
        app.Directory.FailWith = "GraphUnavailable";
        for (var i = 0; i < 4; i++) await app.RunWorker();
        Assert.Equal(RequestStatus.FailedNeedsDev, await app.Status(id));

        var path = $"projects/{TestApp.Alpha}/requests/{id}/resume";
        Assert.Equal(403, (await app.Call(TestApp.Ivan, "POST", path)).Status);
        app.Directory.FailWith = null; // the developer fixed the cause
        var resumed = await app.Call(TestApp.Oscar, "POST", path);
        Assert.Equal(200, resumed.Status);

        var flow = (await app.Request(id)).Flow!;
        Assert.Contains(flow.Events, e => e.Step == "resume");
        Assert.Empty(flow.Attempts);
        Assert.NotNull(flow.IdTeamTask!.IdReport); // the team's report is kept, not redone
        await app.RunWorker();
        Assert.Equal(RequestStatus.WithRoleTeam, await app.Status(id));
        Assert.DoesNotContain(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper && i.Status == IssueStatus.Open);
        Assert.Equal(409, (await app.Call(TestApp.Oscar, "POST", path)).Status);
    }

    [Fact]
    public async Task Resuming_a_failed_review_starts_a_new_review_and_never_skips_the_approvals()
    {
        var down = true;
        var rules = new RuleOnlyReviewModel();
        var app = TestApp.With(new FakeModel((ctx, ct) => down ? throw new HttpRequestException("model down") : rules.ReviewAsync(ctx, new(), ct)));
        var id = await app.SubmitId();
        for (var i = 0; i < 5; i++) await app.RunWorker();
        Assert.Equal(RequestStatus.FailedNeedsDev, await app.Status(id));

        down = false;
        Assert.Equal(200, (await app.Call(TestApp.Oscar, "POST", $"projects/{TestApp.Alpha}/requests/{id}/resume")).Status);
        await app.RunWorker();

        var r = await app.Request(id);
        Assert.Equal(RequestStatus.AwaitingOwner, r.Flow!.Status);
        Assert.Empty(r.Flow.Approvals);
        var runs = (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Where(x => x.Value.RequestId == id).Select(x => x.Value).ToList();
        Assert.Contains(runs, x => x.Trigger == "resume"); // new review in the API, Entra cross-check finished by the worker
        Assert.Equal(ReviewOutcome.Pass, runs.Single(x => x.Id == r.Flow.Scope!.ReviewRunId).Outcome);
    }

    [Fact]
    public async Task Repeated_worker_runs_are_idempotent()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        var before = (await app.Request(id)).Flow!;
        for (var i = 0; i < 4; i++) await app.RunWorker();
        var after = (await app.Request(id)).Flow!;
        Assert.Equal(before.Events.Count, after.Events.Count);
        Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.FlowOverview);
        Assert.Equal(2, (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Count(r => r.Value.RequestId == id)); // API pre-check + Entra cross-check
    }

    [Fact]
    public async Task Notification_ids_are_derived_from_request_id_and_step()
    {
        var key = Ids.Key("req-1", "owner-approval:run-1");
        Assert.Equal("req-1:owner-approval:run-1", key);
        Assert.Equal(Notification.MakeId(key, NotificationKind.ApprovalRequested), Notification.MakeId(key, NotificationKind.ApprovalRequested));
        Assert.NotEqual(Notification.MakeId(key, NotificationKind.ApprovalRequested), Notification.MakeId(key, NotificationKind.Reminder));
        Assert.Matches("^[0-9a-f]{32}$", Notification.MakeId(key, NotificationKind.Reminder));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Api_with_an_entra_reader_completes_the_review_inline()
    {
        var app = TestApp.With(apiDirectory: FakeDirectory.SeededFromSample());
        var id = await app.SubmitId();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    [Fact]
    public async Task Local_development_can_skip_entra_but_says_so()
    {
        var app = TestApp.With(settings: new PilotSettings { Workflow = new WorkflowSettings { AllowUnverifiedEntra = true } });
        var id = await app.SubmitId();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        var run = (await app.Stores.Runs.GetAsync(TestApp.Alpha, (await app.Request(id)).Flow!.ReviewRunId!))!.Value;
        Assert.Contains("local development", run.Result!.Checks.Single(c => c.Code == CheckCodes.EntraGroup).Detail);
    }

    [Fact]
    public void Unverified_entra_is_refused_outside_local_development()
    {
        Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => k == "ALLOW_UNVERIFIED_ENTRA" ? "true" : null));
        var local = PilotSettings.FromEnvironment(k => k is "ALLOW_UNVERIFIED_ENTRA" or "PILOT_LOCAL_DEV" ? "true" : null);
        Assert.True(local.Workflow.AllowUnverifiedEntra);
    }

    [Fact]
    public void Workflow_settings_come_from_environment_with_safe_bounds()
    {
        var s = PilotSettings.FromEnvironment(k => k switch
        {
            "OWNER_SLA_HOURS" => "8",
            "TAP_MAX_LIFETIME_MINUTES" => "5000",
            "APP_URL" => "http://not-https.invalid",
            _ => null,
        });
        Assert.Equal(TimeSpan.FromHours(8), s.Workflow.OwnerSla);
        Assert.Equal(480, s.Workflow.TapMaxLifetimeMinutes);
        Assert.Null(s.Workflow.AppUrl);
        Assert.Equal(TimeSpan.FromHours(24), s.Workflow.SecuritySla);
    }

    [Fact]
    public async Task Stuck_review_is_recovered_by_the_worker_and_retried()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var model = new FakeModel(async (ctx, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await gate.Task.WaitAsync(TimeSpan.FromMilliseconds(50), ct).ContinueWith(_ => { }, ct);
            return await new RuleOnlyReviewModel().ReviewAsync(ctx, new ReviewLimits(), ct);
        });
        var app = TestApp.With(model, new PilotSettings { Dispatcher = "Queue", AllowPaidServices = true }, queue: new NullQueue());
        var id = await app.SubmitId();
        Assert.Equal(RequestStatus.AiReview, await app.Status(id)); // queued, nothing processed it
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        await app.RunWorker(); // recovers the stuck run (Failed Timeout) and retries it inline on the worker
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    private sealed class NullQueue : PmoReview.Adapters.Dispatch.IQueueClient
    {
        public Task SendAsync(string message, CancellationToken ct) => Task.CompletedTask;
    }
}
