using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

public class MappingSheetTests
{
    private static object Row(string rowId = "A-NEW-ROW") => new
    {
        rowId, resource = "SAMPLE New Thing", accessLevel = "Read", entraGroup = "SAMPLE-GRP-ALPHA-NEW-READ", role = "SAMPLE Reader",
        sourceReference = "SAMPLE-PMO-SHEET row 20", groupState = "ToBeCreated",
    };

    [Fact]
    public async Task Pmo_edits_the_mapping_sheet_and_every_change_bumps_the_version()
    {
        var app = TestApp.Create();
        var created = await app.Call(TestApp.Paul, "POST", $"projects/{TestApp.Alpha}/mapping-sheet", Row());
        Assert.Equal(201, created.Status);
        Assert.Equal(1, created.Value.GetProperty("version").GetInt32());
        var upd = await app.Call(TestApp.Paul, "PUT", $"projects/{TestApp.Alpha}/mapping-sheet/A-NEW-ROW",
            new { resource = "SAMPLE New Thing", accessLevel = "Read", entraGroup = "SAMPLE-GRP-ALPHA-NEW-READ", role = "SAMPLE Reader", sourceReference = "x", deprecated = true }, created.ETag);
        Assert.Equal(200, upd.Status);
        Assert.Equal(2, upd.Value.GetProperty("version").GetInt32());
        Assert.True(upd.Value.GetProperty("deprecated").GetBoolean());
        Assert.Equal(409, (await app.Call(TestApp.Paul, "POST", $"projects/{TestApp.Alpha}/mapping-sheet", Row())).Status);
        Assert.Equal(412, (await app.Call(TestApp.Paul, "PUT", $"projects/{TestApp.Alpha}/mapping-sheet/A-NEW-ROW", Row(), created.ETag)).Status);
    }

    [Theory]
    [InlineData(TestApp.Alice)]
    [InlineData(TestApp.Sam)]
    [InlineData(TestApp.Ivan)]
    [InlineData(TestApp.Oscar)]
    public async Task Only_pmo_adds_rows(string user)
    {
        var app = TestApp.Create();
        Assert.Equal(403, (await app.Call(user, "POST", $"projects/{TestApp.Alpha}/mapping-sheet", Row())).Status);
    }

    [Fact]
    public async Task Id_team_changes_only_the_group_of_a_row()
    {
        var app = TestApp.Create();
        var sheet = await app.Call(TestApp.Ivan, "GET", $"projects/{TestApp.Alpha}/mapping-sheet");
        var item = sheet.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("value").GetProperty("rowId").GetString() == "A-FIN-VIEW");
        var r = await app.Call(TestApp.Ivan, "POST", $"projects/{TestApp.Alpha}/mapping-sheet/A-FIN-VIEW/group",
            new { entraGroup = "SAMPLE-GRP-ALPHA-FIN-VIEWERS", groupState = "Exists", role = "Owner" }, item.GetProperty("etag").GetString());
        Assert.Equal(200, r.Status);
        Assert.Equal("SAMPLE-GRP-ALPHA-FIN-VIEWERS", r.Value.GetProperty("entraGroup").GetString());
        Assert.Equal("SAMPLE Report Viewer", r.Value.GetProperty("role").GetString()); // role untouched
        Assert.Equal(TestApp.Ivan, r.Value.GetProperty("updatedBy").GetString());
        Assert.Equal(403, (await app.Call(TestApp.Paul, "POST", $"projects/{TestApp.Alpha}/mapping-sheet/A-FIN-VIEW/group", new { groupState = "Exists" }, r.ETag)).Status);
    }

    [Fact]
    public async Task Response_document_is_readable_by_project_roles_and_not_writable_through_the_api()
    {
        var app = TestApp.Create();
        var r = await app.Call(TestApp.Rose, "GET", $"projects/{TestApp.Alpha}/response-document");
        Assert.Equal(200, r.Status);
        Assert.Equal(4, r.Json.GetProperty("items").GetArrayLength());
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Alpha}/response-document")).Status);
        Assert.DoesNotContain(app.Host.Routes, route => route.StartsWith("POST projects/{projectId}/response-document", StringComparison.Ordinal)
                                                      || route.StartsWith("PUT projects/{projectId}/response-document", StringComparison.Ordinal));
    }
}

public class ReportsTests
{
    [Fact]
    public async Task Timings_split_per_stage_and_per_team_including_wait_time()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId();
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await app.RunWorker();
        app.Clock.Advance(TimeSpan.FromHours(2));
        await app.Approve(id, TestApp.Olivia);
        app.Clock.Advance(TimeSpan.FromHours(1));
        var t = Reports.Timings(await app.Request(id), app.Clock.UtcNow);
        Assert.Equal(7200, t.PerTeam.Single(x => x.Name == "Owner").Seconds);
        Assert.Equal(3600, t.PerTeam.Single(x => x.Name == "Security").Seconds);
        Assert.Equal(300, t.PerTeam.Single(x => x.Name == "AI agent").Seconds);
        Assert.Equal(3 * 3600 + 300, t.Total.Seconds);
        Assert.Equal("Awaiting Owner", t.PerStage.Single(x => x.Seconds == 7200).Name);
    }

    [Fact]
    public async Task Daily_summary_counts_the_day_and_wait_time_per_team()
    {
        var app = TestApp.Create();
        var done = await app.To(RequestStatus.Verifying);
        app.UserCompletesFirstSignIn();
        await app.RunWorker();
        var returned = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e5005", resource: "SAMPLE Legacy Tool"));
        var waiting = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e6006"));
        await app.RunWorker();
        app.Clock.Advance(TimeSpan.FromHours(3));

        var r = await app.Call(TestApp.Oscar, "GET", $"projects/{TestApp.Alpha}/reports/daily/2026-10-17");
        Assert.Equal(200, r.Status);
        var j = r.Json;
        Assert.Equal(3, j.GetProperty("submitted").GetInt32());
        Assert.Equal(1, j.GetProperty("completed").GetInt32());
        Assert.Equal(1, j.GetProperty("returned").GetInt32());
        Assert.Contains(j.GetProperty("waitTimePerTeam").EnumerateArray(), w => w.GetProperty("name").GetString() == "Owner" && w.GetProperty("seconds").GetInt64() >= 3 * 3600);
        Assert.Contains("Wait time per team today", j.GetProperty("text").GetString());
        Assert.Equal(403, (await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/reports/daily/2026-10-17")).Status);
        Assert.Equal(400, (await app.Call(TestApp.Oscar, "GET", $"projects/{TestApp.Alpha}/reports/daily/17-10-2026")).Status);
        Assert.NotNull(done + returned + waiting);
    }

    [Fact]
    public async Task Daily_summary_is_sent_once_per_day_by_the_worker()
    {
        var app = TestApp.Create();
        await app.SubmitId();
        app.Clock.Advance(TimeSpan.FromDays(1)); // next day 09:00 UTC, after the send hour
        await app.RunWorker();
        await app.RunWorker();
        var daily = (await app.Notifications()).Where(n => n.Kind == NotificationKind.DailySummary).ToList();
        Assert.Single(daily);
        Assert.Contains("sample.operator.alpha@sample.invalid", daily[0].To);
        Assert.Contains("Daily summary SAMPLE-ALPHA — 2026-10-17", daily[0].Body);
    }

    [Fact]
    public async Task Developer_report_lists_failed_requests_with_attempts_and_last_events()
    {
        var app = TestApp.With(new FakeModel((_, _) => throw new HttpRequestException("down")));
        var id = await app.SubmitId();
        for (var i = 0; i < 4; i++) await app.RunWorker();
        var r = await app.Call(TestApp.Oscar, "GET", $"projects/{TestApp.Alpha}/reports/developer");
        Assert.Equal(200, r.Status);
        var item = Assert.Single(r.Json.GetProperty("requests").EnumerateArray());
        Assert.Equal(id, item.GetProperty("requestId").GetString());
        Assert.Contains("attempts failed", item.GetProperty("reason").GetString());
        Assert.Contains("Developer issue report", item.GetProperty("report").GetString());
    }

    [Fact]
    public async Task Trail_is_the_full_masked_audit_with_analysis()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithRoleTeam);
        var r = await app.Call(TestApp.Oscar, "GET", $"projects/{TestApp.Alpha}/requests/{id}/trail");
        var text = r.Json.GetProperty("text").GetString()!;
        foreach (var part in new[] { "[review] run", "Owner Approved via App", "Security Approved via App", "ID team reported: user sample.e1001@sample.invalid",
                     "TAP issued", "one-time=True", "[notify] ApprovalRequested", "Analysis:", "Longest wait" })
            Assert.Contains(part, text);
    }

    [Fact]
    public void Masker_hides_passes_secrets_and_tokens_but_keeps_ids_and_hashes()
    {
        var hash = new string('a', 64);
        var text = $"TAP: Abc-123-Xyz, password=Hunter2 token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXNpZw AccountKey=abc123def456== request 0123456789abcdef0123456789abcdef hash {hash} key ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuv";
        var masked = SecretMasker.Apply(text)!;
        foreach (var secret in new[] { "Abc-123-Xyz", "Hunter2", "eyJhbGciOiJIUzI1NiJ9", "abc123def456", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuv" })
            Assert.DoesNotContain(secret, masked);
        Assert.Contains("0123456789abcdef0123456789abcdef", masked);
        Assert.Contains(hash, masked);
        Assert.Contains("TAP: ******", masked);
    }

    [Fact]
    public async Task Notification_bodies_never_contain_a_pass_even_if_one_is_put_in_free_text()
    {
        var app = TestApp.Create();
        var notifier = new Notifier(app.Stores, app.Clock, NullLogger<Notifier>.Instance);
        await notifier.EnqueueAsync(TestApp.Alpha, "req-x", "step", NotificationKind.Closure, ["a@sample.invalid"], "Temporary Access Pass: Q1w2-E3r4", "your pass is Q1w2-E3r4", CancellationToken.None);
        var n = (await app.Notifications("req-x")).Single();
        Assert.DoesNotContain("Q1w2-E3r4", n.Subject + n.Body);
    }
}

public class OutboundTests
{
    [Fact]
    public async Task Pilot_sender_keeps_notifications_in_the_app_as_recorded()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId();
        await app.RunWorker();
        Assert.All(await app.Notifications(id), n => Assert.Equal(NotificationStatus.Recorded, n.Status));
    }

    [Fact]
    public async Task Failing_sender_retries_three_times_then_marks_failed_and_logs_an_issue()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId();
        var dispatcher = new NotificationDispatcher(app.Stores, new FailingSender(), app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance);
        for (var i = 0; i < 4; i++) await dispatcher.RunOnceAsync(CancellationToken.None);
        var n = Assert.Single(await app.Notifications(id), x => x.Kind == NotificationKind.FlowOverview);
        Assert.Equal(NotificationStatus.Failed, n.Status);
        Assert.Equal(3, n.Attempts);
        Assert.Equal("MailUnavailable", n.LastError);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper);
    }

    [Fact]
    public async Task Ado_work_item_is_created_once_per_request_and_updated_with_new_events()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        await app.RunWorker();
        var first = app.Tracker.Drafts.Where(d => d.RequestId == id).ToList();
        Assert.NotEmpty(first);
        Assert.Equal($"pmo-request-{id}", first[0].Tag);
        Assert.Contains("[Awaiting Owner]", first.Last().Title);
        var link = (await app.Request(id)).Flow!.Ado!;
        Assert.Equal(1000, link.WorkItemId);

        await app.RunWorker();
        Assert.Equal(first.Count, app.Tracker.Drafts.Count(d => d.RequestId == id)); // nothing new: no update

        await app.Approve(id, TestApp.Olivia);
        await app.RunWorker();
        var update = app.Tracker.Drafts.Last(d => d.RequestId == id);
        Assert.Contains(update.NewHistory, h => h.Contains("owner-approved", StringComparison.Ordinal));
        Assert.Equal(1000, (await app.Request(id)).Flow!.Ado!.WorkItemId);
    }

    [Fact]
    public async Task Ado_failures_do_not_block_the_request_and_stop_after_three_attempts()
    {
        var app = TestApp.Create();
        app.Tracker.FailWith = "AdoUnavailable";
        var id = await app.To(RequestStatus.AwaitingOwner);
        for (var i = 0; i < 4; i++) await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.AwaitingOwner, flow.Status);
        Assert.Equal("AdoUnavailable", flow.Ado!.Error);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper && i.Step == "ado");
    }

    [Fact]
    public async Task Disabled_tracker_leaves_no_ado_link()
    {
        var app = TestApp.Create();
        app.Tracker.Enabled = false;
        var id = await app.To(RequestStatus.AwaitingOwner);
        await app.RunWorker();
        Assert.Null((await app.Request(id)).Flow!.Ado);
        Assert.Empty(app.Tracker.Drafts);
    }

    private sealed class FailingSender : INotificationSender
    {
        public string Name => "Failing";
        public bool Delivers => true;
        public Task SendAsync(Notification notification, CancellationToken ct) => throw new OutboundException("MailUnavailable");
    }
}
