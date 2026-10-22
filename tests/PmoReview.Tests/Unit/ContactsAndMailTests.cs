using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// The contact inventory (Security, ID team and role assignment team mail lists, semicolon-separated, edited in the app),
/// who each mail goes to, and the mail limit of the Azure-managed domain (F70) — the go-live notification rules of 22 Oct 2026.
/// </summary>
public class ContactsAndMailTests
{
    private static string Path => $"projects/{TestApp.Alpha}/contacts";

    // ---- Mail addresses -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Jane.Doe@Contoso.com", "jane.doe@contoso.com")]
    [InlineData("  Doe, Jane <jane.doe@contoso.com> ", "jane.doe@contoso.com")]
    [InlineData("mailto:sec@contoso.com", "sec@contoso.com")]
    [InlineData("owner_gmail.com#EXT#@tenant.onmicrosoft.com", "owner@gmail.com")]
    [InlineData("live.com#owner@outlook.com", "owner@outlook.com")]
    [InlineData("sample-user-alice", null)]
    [InlineData("two@@contoso.com", null)]
    [InlineData("nodot@contoso", null)]
    [InlineData("a b@contoso.com", null)]
    [InlineData("", null)]
    public void A_mail_address_is_normalised_or_refused(string text, string? expected) => Assert.Equal(expected, MailAddresses.Normalize(text));

    [Fact]
    public void A_team_list_is_split_on_semicolons_and_keeps_outlook_display_names_whole()
    {
        var (valid, invalid) = MailAddresses.ParseList("Doe, Jane <jane@contoso.com>; sec@contoso.com;; SEC@contoso.com\nops@contoso.com, ops2@contoso.com; not-an-address");
        Assert.Equal(["jane@contoso.com", "sec@contoso.com", "ops@contoso.com", "ops2@contoso.com"], valid); // de-duplicated, in order
        Assert.Equal(["not-an-address"], invalid);
    }

    // ---- Contact inventory page -----------------------------------------------------------------------------------

    [Fact]
    public async Task Everyone_in_the_project_reads_the_contact_inventory_only_pmo_and_operators_change_it()
    {
        var app = TestApp.Create();
        var read = await app.Call(TestApp.Alice, "GET", Path);
        Assert.Equal(200, read.Status);
        Assert.Equal(["sample.security.alpha@sample.invalid"], read.Value.GetProperty("contacts").GetProperty("security").EnumerateArray().Select(x => x.GetString()));
        Assert.False(read.Value.GetProperty("canEdit").GetBoolean());

        foreach (var who in new[] { TestApp.Alice, TestApp.Sam, TestApp.Ivan, TestApp.Olivia })
            Assert.Equal(403, (await app.Call(who, "PUT", Path, new { security = "x@contoso.com" }, read.ETag)).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", Path)).Status); // another project's person: the project is not revealed
        Assert.True((await app.Call(TestApp.Paul, "GET", Path)).Value.GetProperty("canEdit").GetBoolean());
        Assert.True((await app.Call(TestApp.Oscar, "GET", Path)).Value.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Pmo_saves_semicolon_lists_and_the_next_mails_go_to_the_new_addresses()
    {
        var app = TestApp.Create();
        var etag = (await app.Call(TestApp.Paul, "GET", Path)).ETag;
        var saved = await app.Call(TestApp.Paul, "PUT", Path, new
        {
            security = "Sec One <sec1@contoso.com>; sec2@contoso.com",
            idTeam = "id@contoso.com",
            roleTeam = "roles@contoso.com; roles2@contoso.com",
            securityManager = "ciso@contoso.com",
        }, etag);
        Assert.Equal(200, saved.Status);
        var c = saved.Value.GetProperty("contacts");
        Assert.Equal(["sec1@contoso.com", "sec2@contoso.com"], c.GetProperty("security").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["sample.pmo.alpha@sample.invalid"], c.GetProperty("pmo").EnumerateArray().Select(x => x.GetString())); // not sent: kept
        Assert.Equal(1, saved.Value.GetProperty("version").GetInt32());
        Assert.Equal(TestApp.SignIn(TestApp.Paul), saved.Value.GetProperty("updatedBy").GetString());

        // The change is mailed to Security and PMO, before and after (a removed Security address learns of it).
        var changed = Assert.Single(await app.Notifications(), n => n.Kind == NotificationKind.SecurityAlert && n.Subject.StartsWith("Contact inventory changed", StringComparison.Ordinal));
        foreach (var a in new[] { "sample.security.alpha@sample.invalid", "sec1@contoso.com", "sec2@contoso.com", "sample.pmo.alpha@sample.invalid" })
            Assert.Contains(a, changed.To);
        Assert.Contains("removed sample.security.alpha@sample.invalid", changed.Body);

        // The flow uses the new lists at once.
        var id = await app.To(RequestStatus.WithIdTeam);
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested && n.To.SequenceEqual(["sec1@contoso.com", "sec2@contoso.com"]));
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.To.SequenceEqual(["id@contoso.com"]));
    }

    [Fact]
    public async Task A_bad_entry_or_an_empty_required_team_is_refused_with_every_problem_and_nothing_changes()
    {
        var app = TestApp.Create();
        var etag = (await app.Call(TestApp.Paul, "GET", Path)).ETag;
        var bad = await app.Call(TestApp.Paul, "PUT", Path, new { security = "", idTeam = "id@contoso.com; ivan", roleTeam = "roles@contoso", securityManager = "a@contoso.com; b@contoso.com" }, etag);
        Assert.Equal(400, bad.Status);
        var details = bad.Json.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()!).ToList();
        Assert.Contains(details, d => d.StartsWith("Security: at least one address", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Contains("'ivan' is not a mail address"));
        Assert.Contains(details, d => d.Contains("'roles@contoso' is not a mail address"));
        Assert.Contains(details, d => d.StartsWith("Security manager: one mail address", StringComparison.Ordinal));
        Assert.Equal(["sample.security.alpha@sample.invalid"], (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!.Value.Contacts.Security);

        var tooMany = string.Join(";", Enumerable.Range(0, MailAddresses.MaxPerList + 1).Select(i => $"u{i}@contoso.com"));
        Assert.Equal(400, (await app.Call(TestApp.Paul, "PUT", Path, new { security = tooMany }, etag)).Status);
    }

    [Fact]
    public async Task Saving_needs_the_current_etag_and_an_unchanged_save_creates_no_version()
    {
        var app = TestApp.Create();
        var first = await app.Call(TestApp.Paul, "GET", Path);
        Assert.Equal(428, (await app.Call(TestApp.Paul, "PUT", Path, new { security = "s@contoso.com" })).Status);
        var saved = await app.Call(TestApp.Paul, "PUT", Path, new { security = "s@contoso.com" }, first.ETag);
        Assert.Equal(200, saved.Status);
        Assert.Equal(412, (await app.Call(TestApp.Oscar, "PUT", Path, new { security = "t@contoso.com" }, first.ETag)).Status); // someone saved meanwhile

        var same = await app.Call(TestApp.Paul, "PUT", Path, new { security = "S@contoso.com" }, saved.ETag);
        Assert.Equal(1, same.Value.GetProperty("version").GetInt32());
        Assert.Single(await app.Notifications(), n => n.Kind == NotificationKind.SecurityAlert && n.Subject.StartsWith("Contact inventory changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_page_names_the_gaps_that_stop_mails()
    {
        var app = TestApp.Create();
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { Contacts = new ProjectContacts() }, p.ETag);
        var gaps = (await app.Call(TestApp.Alice, "GET", Path)).Value.GetProperty("gaps").EnumerateArray().Select(g => g.GetString()!).ToList();
        Assert.Contains(gaps, g => g.StartsWith("No Security address", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.StartsWith("No ID team address", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.StartsWith("No role assignment team address", StringComparison.Ordinal));
    }

    // ---- Who gets what -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_is_mailed_only_to_decide_and_the_requester_gets_every_outcome()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.Completed);
        var notes = await app.Notifications(id);
        var owner = "sample-user-olivia@sample.invalid";
        Assert.All(notes.Where(n => n.To.Contains(owner)), n => Assert.Equal(NotificationKind.ApprovalRequested, n.Kind));
        var requester = TestApp.SignIn(TestApp.Alice);
        foreach (var k in new[] { NotificationKind.FlowOverview, NotificationKind.CompletionSummary })
            Assert.Contains(notes, n => n.Kind == k && n.To.Contains(requester));
    }

    [Fact]
    public async Task A_guest_requester_is_mailed_at_the_mailbox_not_the_guest_sign_in_name()
    {
        var app = TestApp.Create();
        var res = await app.CallAs(TestApp.Alice, "alice_partner.example#EXT#@pilot.onmicrosoft.com", "POST", $"projects/{TestApp.Alpha}/requests", TestApp.Intake());
        Assert.Equal(201, res.Status);
        var id = res.Request.GetProperty("id").GetString()!;
        var overview = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.FlowOverview);
        Assert.Contains("alice@partner.example", overview.To);
    }

    [Fact]
    public async Task An_entry_that_is_not_a_mail_address_is_skipped_and_the_rest_are_still_mailed()
    {
        var app = TestApp.Create();
        var notifier = app.Worker.Notifier;
        await notifier.EnqueueAsync(TestApp.Alpha, "req-y", "step", NotificationKind.TeamTask, ["ivan", "id@contoso.com", "ID@contoso.com", null], "s", "b", CancellationToken.None);
        var n = (await app.Notifications("req-y")).Single();
        Assert.Equal(["id@contoso.com"], n.To);
        Assert.Equal(["ivan"], n.Skipped);
        Assert.Null(n.LastError);
    }

    [Fact]
    public async Task With_mail_on_an_action_mail_that_reaches_nobody_raises_a_high_issue()
    {
        var app = TestApp.With(settings: new PilotSettings { Workflow = new WorkflowSettings { MailEnabled = true } });
        await app.Worker.Notifier.EnqueueAsync(TestApp.Alpha, "req-z", "approval", NotificationKind.ApprovalRequested, [], "s", "b", CancellationToken.None);
        await app.Worker.Notifier.EnqueueAsync(TestApp.Alpha, "req-z", "info", NotificationKind.FlowOverview, [], "s", "b", CancellationToken.None);
        var issue = Assert.Single(await app.Issues("req-z"));
        Assert.Equal((IssueKind.NeedsDeveloper, IssueSeverity.High), (issue.Kind, issue.Severity));
        Assert.Contains("Contact inventory", issue.Summary);
    }

    // ---- The mail limit (Azure-managed domain: 5 a minute, 10 an hour) ----------------------------------------------

    private sealed class CountingSender(int throttleAfter = int.MaxValue) : INotificationSender
    {
        public List<Notification> Sent { get; } = [];
        public string Name => "Counting";
        public bool Delivers => true;
        public Task SendAsync(Notification n, CancellationToken ct)
        {
            if (Sent.Count >= throttleAfter) throw new OutboundException("MailThrottled");
            Sent.Add(n);
            return Task.CompletedTask;
        }
    }

    private static async Task Queue(TestApp app, int count, NotificationKind kind, string prefix)
    {
        for (var i = 0; i < count; i++)
        {
            await app.Worker.Notifier.EnqueueAsync(TestApp.Alpha, $"{prefix}-{i}", "s", kind, ["a@contoso.com"], $"{kind} {i}", "b", CancellationToken.None);
            app.Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Over_the_mail_limit_mails_wait_most_urgent_first_and_none_is_lost_or_failed()
    {
        var app = TestApp.Create();
        await Queue(app, 6, NotificationKind.FlowOverview, "info");
        await Queue(app, 3, NotificationKind.ApprovalRequested, "ask");
        var sender = new CountingSender();
        var dispatcher = new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance, limits: MailLimits.AzureManagedDomain);

        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(5, sender.Sent.Count); // 5 a minute
        Assert.All(sender.Sent.Take(3), n => Assert.Equal(NotificationKind.ApprovalRequested, n.Kind)); // asks for a decision first

        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(9, sender.Sent.Count);

        await Queue(app, 3, NotificationKind.FlowOverview, "more");
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(10, sender.Sent.Count); // 10 an hour
        var waiting = (await app.Notifications()).Where(n => n.Status == NotificationStatus.Pending).ToList();
        Assert.Equal(2, waiting.Count);
        Assert.All(waiting, n => Assert.Equal(0, n.Attempts)); // waiting is not failing

        app.Clock.Advance(TimeSpan.FromHours(1));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal(12, sender.Sent.Count);
        Assert.DoesNotContain(await app.Notifications(), n => n.Status is NotificationStatus.Pending or NotificationStatus.Failed);
    }

    [Fact]
    public async Task A_throttled_send_stops_the_run_uses_no_attempt_and_an_hour_of_backlog_raises_a_warning_that_clears()
    {
        var app = TestApp.Create();
        await Queue(app, 4, NotificationKind.TeamTask, "task");
        var sender = new CountingSender(throttleAfter: 1);
        var dispatcher = new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance, limits: MailLimits.None);
        for (var i = 0; i < 5; i++) await dispatcher.RunOnceAsync(CancellationToken.None);
        Assert.Single(sender.Sent);
        var held = (await app.Notifications()).Where(n => n.Status == NotificationStatus.Pending).ToList();
        Assert.Equal(3, held.Count);
        Assert.All(held, n => Assert.Equal(0, n.Attempts));
        Assert.Contains(held, n => n.LastError == "MailThrottled");

        app.Clock.Advance(TimeSpan.FromMinutes(61));
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var backlog = Assert.Single(await app.Issues(), i => i.Step == NotificationDispatcher.BacklogStep);
        Assert.Equal((IssueStatus.Open, IssueSeverity.Warning), (backlog.Status, backlog.Severity));

        var ok = new NotificationDispatcher(app.Stores, new CountingSender(), app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance);
        await ok.RunOnceAsync(CancellationToken.None);
        Assert.Equal(IssueStatus.Resolved, Assert.Single(await app.Issues(), i => i.Step == NotificationDispatcher.BacklogStep).Status);
    }

    [Fact]
    public async Task Mails_sent_directly_count_against_the_limit_and_the_reserve_keeps_room_for_the_sign_in_details()
    {
        var app = TestApp.Create();
        await Queue(app, 10, NotificationKind.FlowOverview, "info");
        var sender = new CountingSender();
        var dispatcher = new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance,
            limits: MailLimits.AzureManagedDomain, reserve: 2);
        for (var i = 0; i < 4; i++)
        {
            await dispatcher.RunOnceAsync(CancellationToken.None);
            app.Clock.Advance(TimeSpan.FromMinutes(5));
        }
        Assert.Equal(8, sender.Sent.Count); // 10 an hour minus the 2 kept for the agent's sign-in details
        Assert.Equal(2, await MailBudget.RoomAsync(app.Stores, app.Clock, MailLimits.AzureManagedDomain, 0, CancellationToken.None));
        // The agent's sign-in details mail (sent directly, recorded masked) uses one of them.
        await app.Worker.Notifier.RecordSentAsync(TestApp.Alpha, "req-tap", "sign-in", NotificationKind.SignInDetails, ["joiner@contoso.com"], "s", "******",
            ["joiner@contoso.com"], CancellationToken.None);
        Assert.Equal(1, await MailBudget.RoomAsync(app.Stores, app.Clock, MailLimits.AzureManagedDomain, 0, CancellationToken.None));
    }

    [Fact]
    public void The_mail_limit_defaults_to_the_azure_managed_domain_and_can_be_raised_for_a_custom_domain()
    {
        Assert.Equal(new MailLimits(5, 10), PilotSettings.FromEnvironment(_ => null).Workflow.MailLimits);
        var custom = PilotSettings.FromEnvironment(n => n switch { "MAIL_MAX_PER_MINUTE" => "30", "MAIL_MAX_PER_HOUR" => "100", _ => null });
        Assert.Equal(new MailLimits(30, 100), custom.Workflow.MailLimits);
    }
}
