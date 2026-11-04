using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Domain;
using PmoReview.Core.Processing;
using PmoReview.Core.Resources;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// 4 Nov 2026, live: "the site did not start the agent". Edge cases where a person's action on the website was saved but
/// the agent's next step (or the mail that tells people) never came, found by an independent read of the API path and the
/// worker. Each test failed before its fix.
/// </summary>
public class AgentStartEdgeCaseTests
{
    private static async Task<IReadOnlyList<Notification>> Mails(TestApp app, string requestId, NotificationKind kind, string project = TestApp.Alpha) =>
        (await app.Stores.Notifications.ListAsync(project)).Select(n => n.Value).Where(n => n.RequestId == requestId && n.Kind == kind).ToList();

    [Fact]
    public async Task A_submit_whose_intake_format_was_auto_fixed_still_sends_the_confirmation_mail()
    {
        var app = TestApp.Create();
        // A capital letter in the Enterprise ID and a name in lower case: the intake is auto-fixed (logged after "submit").
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "Sample.E1001@Sample-Corp.invalid", first: "sample", last: "JOINER"));
        var r = await app.Request(id);
        Assert.Contains(r.Flow!.Events, e => e.Step == WorkflowAgent.IntakeFormatStep);

        Assert.Single(await Mails(app, id, NotificationKind.FlowOverview)); // was none: the trigger read "intake-format"
        var run = Assert.Single(await app.Stores.Runs.ListAsync(TestApp.Alpha), x => x.Value.RequestId == id).Value;
        Assert.Equal("submit", run.Trigger);
    }

    [Fact]
    public async Task A_review_run_left_queued_is_processed_on_the_next_agent_step_not_after_stuck_run_recovery()
    {
        var mem = InMemoryDataStores.Create();
        var runs = new FaultyStore<ReviewRun>(mem.Runs);
        var app = TestApp.On(mem with { Runs = runs });
        var claims = 0;
        // The inline claim (Queued → Running) fails once, e.g. a Cosmos error: the run stays Queued, the request Submitted.
        runs.Fail = (op, _) => op == "Replace" && claims++ == 0 ? new InvalidOperationException("SAMPLE Cosmos write failed") : null;
        var submit = await app.Submit();
        Assert.Equal(201, submit.Status); // saved; the error is on the issues list
        var id = submit.Request.GetProperty("id").GetString()!;
        Assert.Equal(RequestStatus.Submitted, await app.Status(id));
        Assert.Equal(ProcessingStatus.Queued, Assert.Single(await mem.Runs.ListAsync(TestApp.Alpha)).Value.ProcessingStatus);

        await app.RunWorker(); // well within the stuck-run time (5 minutes): before the fix the run was reused and never processed

        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Single(await mem.Runs.ListAsync(TestApp.Alpha)); // the same run, processed once
    }

    [Fact]
    public async Task An_owner_approval_whose_follow_up_fails_is_kept_and_security_is_still_asked()
    {
        var mem = InMemoryDataStores.Create();
        var notes = new FaultyStore<Notification>(mem.Notifications);
        var app = TestApp.On(mem with { Notifications = notes });
        var id = await app.To(RequestStatus.AwaitingOwner);
        var runId = (await app.Request(id)).Flow!.Scope!.ReviewRunId;
        var securityAsk = Notification.MakeId(Ids.Key(id, $"security-approval:{runId}"), NotificationKind.ApprovalRequested);
        var failed = 0;
        notes.Fail = (op, key) => op == "Create" && key == securityAsk && failed++ == 0 ? new InvalidOperationException("SAMPLE outbox write failed") : null;

        var approve = await app.Approve(id, TestApp.Olivia);

        Assert.True(approve.Status == 200, approve.Body); // was a 500 although the approval counted
        Assert.Equal(RequestStatus.AwaitingSecurity, await app.Status(id));
        Assert.Contains((await app.Stores.Issues.ListAsync(TestApp.Alpha)).Select(i => i.Value),
            i => i.RequestId == id && i.Step == "agent-error:Owner approval:InvalidOperationException");
        Assert.DoesNotContain(await Mails(app, id, NotificationKind.ApprovalRequested), n => n.Id == securityAsk);

        await app.RunWorker(); // the agent asks Security again (once): before the fix only the 24-hour reminder came

        Assert.Single(await Mails(app, id, NotificationKind.ApprovalRequested), n => n.Id == securityAsk);
        await app.RunWorker();
        Assert.Single(await Mails(app, id, NotificationKind.ApprovalRequested), n => n.Id == securityAsk);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status); // and the flow goes on
    }

    private sealed class PickySender : INotificationSender
    {
        public List<string> Sent { get; } = [];
        public string Name => "Picky";
        public bool Delivers => true;

        public Task SendAsync(Notification notification, CancellationToken ct)
        {
            // Like AcsEmailSender with an empty address: an error the sender does not classify as an OutboundException.
            if (notification.To.Contains("bad@sample.invalid")) throw new ArgumentException("SAMPLE: recipient address is empty");
            Sent.Add(notification.Id);
            return Task.CompletedTask;
        }
    }

    private static Notification Pending(string id, string to, DateTimeOffset at) => new()
    {
        Id = id, ProjectId = TestApp.Alpha, Kind = NotificationKind.Reminder, IdempotencyKey = id, To = [to], Subject = "SAMPLE", Body = "SAMPLE",
        CreatedAt = at, Status = NotificationStatus.Pending,
    };

    [Fact]
    public async Task One_mail_the_sender_cannot_take_does_not_stop_the_mails_after_it()
    {
        var app = TestApp.Create();
        await app.Stores.Notifications.CreateAsync(Pending("n-bad", "bad@sample.invalid", app.Clock.UtcNow));
        await app.Stores.Notifications.CreateAsync(Pending("n-good", "good@sample.invalid", app.Clock.UtcNow.AddSeconds(1)));
        var sender = new PickySender();
        var dispatcher = new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.RunOnceAsync(CancellationToken.None); // threw before the fix, on the first (oldest) mail, every run

        Assert.Equal(["n-good"], sender.Sent);
        var bad = (await app.Stores.Notifications.GetAsync(TestApp.Alpha, "n-bad"))!.Value;
        Assert.Equal((NotificationStatus.Pending, 1, "SendError:ArgumentException"), (bad.Status, bad.Attempts, bad.LastError));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(NotificationStatus.Failed, (await app.Stores.Notifications.GetAsync(TestApp.Alpha, "n-bad"))!.Value.Status); // 3 tries, then an issue
        Assert.Contains((await app.Stores.Issues.ListAsync(TestApp.Alpha)).Select(i => i.Value), i => i.Step == "notify:n-bad");
    }

    [Fact]
    public async Task Incomplete_mail_settings_fail_each_mail_with_the_reason_instead_of_every_worker_run()
    {
        var app = TestApp.Create();
        await app.Stores.Notifications.CreateAsync(Pending("n-1", "someone@sample.invalid", app.Clock.UtcNow));
        var sender = new MisconfiguredSender("NOTIFY_SENDER=AcsEmail needs ACS_ENDPOINT");
        await new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance).RunOnceAsync(CancellationToken.None);
        var n = (await app.Stores.Notifications.GetAsync(TestApp.Alpha, "n-1"))!.Value;
        Assert.Equal(NotificationStatus.Pending, n.Status);
        Assert.StartsWith("MailNotConfigured: NOTIFY_SENDER=AcsEmail needs ACS_ENDPOINT", n.LastError);
    }

    [Fact]
    public async Task One_group_name_Graph_refuses_does_not_stop_the_other_names_of_the_project()
    {
        var app = TestApp.Create();
        app.ResolveGroups = true;
        var rows = ResourceResolver.Offerable((await app.Stores.MappingSheet.ListAsync(TestApp.Alpha)).Select(v => v.Value).Where(r => r.ProjectId == TestApp.Alpha),
            null, DateOnly.FromDateTime(app.Clock.UtcNow.UtcDateTime));
        var names = rows.Select(r => r.EntraGroup).Where(n => !string.IsNullOrWhiteSpace(n)).DistinctBy(ResourceResolver.Normalise).ToList();
        Assert.True(names.Count >= 2);
        app.Directory.RefuseGroupNames.Add(ResourceResolver.Label(names[0])); // the first name read

        await app.RunWorker();

        var resolved = (await app.Stores.GroupResolutions.ListAsync(TestApp.Alpha)).Select(v => v.Value.GroupName).ToList();
        Assert.Equal(names.Count - 1, resolved.Count); // was 0: the refused first name ended the project's run every time
        Assert.DoesNotContain(ResourceResolver.Label(names[0]), resolved);
    }
}
