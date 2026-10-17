using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>The whole flow, stage by stage, through the real API (no Entra reader) and the worker (read-only Entra).</summary>
public class FlowTests
{
    [Fact]
    public async Task Request_goes_through_all_seven_stages_to_completed()
    {
        var app = TestApp.Create();

        // 1. Requester submits; the flow overview is stated upfront.
        var submit = await app.Submit();
        Assert.Equal(201, submit.Status);
        var id = submit.Request.GetProperty("id").GetString()!;
        Assert.Equal(7, submit.Json.GetProperty("flowOverview").GetArrayLength());
        Assert.Equal(TestApp.DefaultUser, submit.Request.GetProperty("domainId").GetString()); // derived, not typed

        // 2. AI review: the API runs every check it can; the Entra cross-check waits for the worker. Never sent for approval yet.
        Assert.Equal(RequestStatus.AiReview, await app.Status(id));
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested);
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal(RequestStatus.AwaitingOwner, r.Flow!.Status);
        Assert.Equal(WaitingOn.Owner, r.Flow.WaitingOn);
        Assert.Equal(TestApp.DefaultGroup, r.Flow.Scope!.EntraGroup);
        Assert.Contains("What:", r.Flow.Scope.Summary);
        var ownerAsk = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Equal(["sample-user-olivia@sample.invalid"], ownerAsk.To);
        Assert.Contains("Risk flags:", ownerAsk.Body);
        var record = (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value;
        Assert.Equal(ResponseState.PendingApproval, record.State);
        Assert.Equal(TestApp.DefaultRole, record.Role);

        // 3. Owner approves -> 4. Security approves.
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(RequestStatus.AwaitingSecurity, await app.Status(id));
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested && n.To.Contains("sample.security.alpha@sample.invalid"));
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        r = await app.Request(id);
        Assert.Equal(RequestStatus.WithIdTeam, r.Flow!.Status);
        Assert.Equal(TeamTaskState.Open, r.Flow.IdTeamTask!.State);
        Assert.Contains("ONE-TIME Temporary Access Pass", r.Flow.IdTeamTask.Instructions);
        Assert.Equal(ResponseState.Approved, (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value.State);

        // 5. ID team creates the user, adds the membership, issues the TAP (metadata only) and records it.
        app.Directory.AddUser(TestApp.DefaultUser);
        app.Directory.AddMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        app.Directory.SetTap(TestApp.DefaultUser, new TapMethod(app.Clock.UtcNow, app.Clock.UtcNow, 60, true, true, "EnabledByPolicy"));
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport())).Status);
        r = await app.Request(id);
        Assert.Equal(RequestStatus.WithIdTeam, r.Flow!.Status); // the API cannot read Entra: the worker verifies
        Assert.Equal(WaitingOn.Agent, r.Flow.WaitingOn);
        await app.RunWorker();
        r = await app.Request(id);
        Assert.Equal(RequestStatus.WithRoleTeam, r.Flow!.Status);
        Assert.Equal(TeamTaskState.Verified, r.Flow.IdTeamTask!.State);
        Assert.Contains(r.Flow.IdTeamTask.Checks, c => c.Code == VerifyCodes.EntraMembership && c.Status == CheckStatus.Pass);
        Assert.Contains($"role {TestApp.DefaultRole}", r.Flow.RoleTeamTask!.Instructions);

        // 6. Role assignment team assigns and records it (updates the response document).
        app.Roles.Assign(app.Directory.GroupId(TestApp.DefaultGroup), TestApp.DefaultRole, TestApp.DefaultScope);
        Assert.Equal(200, (await app.ReportRoleTeam(id, TestApp.RoleReport())).Status);
        record = (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value;
        Assert.Equal(ResponseState.Assigned, record.State);
        Assert.Equal(TestApp.SignIn(TestApp.Rose), record.AssignedBy);
        Assert.Equal(RequestStatus.Verifying, await app.Status(id));
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.AccessReady && n.To.Single() == TestApp.DefaultUser);

        // 7. Verify: waits for the user's sign-in and permanent method, and the TAP removal.
        await app.RunWorker();
        r = await app.Request(id);
        Assert.Equal(RequestStatus.Verifying, r.Flow!.Status);
        Assert.Contains(r.Flow.FinalChecks, c => c.Code == VerifyCodes.FinalLogin && c.Status == CheckStatus.Pending);
        app.UserCompletesFirstSignIn();
        await app.RunWorker();
        r = await app.Request(id);
        Assert.Equal(RequestStatus.Completed, r.Flow!.Status);
        Assert.All(r.Flow.FinalChecks, c => Assert.Equal(CheckStatus.Pass, c.Status));

        var kinds = (await app.Notifications(id)).Select(n => n.Kind).ToList();
        foreach (var k in new[] { NotificationKind.FlowOverview, NotificationKind.CompletionSummary, NotificationKind.PmoInformed, NotificationKind.Closure })
            Assert.Contains(k, kinds);
        Assert.All(await app.Notifications(id), n => Assert.NotEqual(NotificationStatus.Pending, n.Status)); // dispatched (pilot: Recorded)

        // Audit: ADO work item linked to the request id with the full trail; time per stage and per team.
        await app.RunWorker();
        var draft = app.Tracker.Drafts.Last(d => d.RequestId == id);
        Assert.True(draft.Close);
        foreach (var part in new[] { "Owner Approved", "Security Approved", "ID team reported", "Role assignment team reported", "justification:", "Time per stage", "Wait time per team" })
            Assert.Contains(part, draft.Description);
        r = await app.Request(id);
        Assert.NotNull(r.Flow!.Ado?.Url);
        var view = await app.View(id);
        Assert.Equal("Completed", view.StatusName);
        Assert.Contains(view.Json.GetProperty("timings").GetProperty("perTeam").EnumerateArray(), t => t.GetProperty("name").GetString() == "Owner");
    }

    [Fact]
    public async Task Status_line_shows_requester_stage_waiting_on_done_next_time_and_ado_link()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingSecurity);
        app.Clock.Advance(TimeSpan.FromHours(3));
        var list = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests");
        var line = list.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("requestId").GetString() == id);
        Assert.Equal(TestApp.SignIn(TestApp.Alice), line.GetProperty("requester").GetString());
        Assert.Equal("Awaiting Security", line.GetProperty("currentStage").GetString());
        Assert.Equal("Security", line.GetProperty("waitingOnLabel").GetString());
        Assert.Equal(["Submitted", "AI review", "Owner approval"], line.GetProperty("done").EnumerateArray().Select(d => d.GetString()!).ToArray());
        Assert.Contains("Security approves", line.GetProperty("next").GetString());
        Assert.Equal("3h 0m", line.GetProperty("timeInStage").GetString());
        Assert.True(line.TryGetProperty("adoLink", out _));
    }

    [Fact]
    public async Task Review_failure_returns_to_requester_and_pmo_with_specific_reasons_and_resubmit_restarts_review()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(resource: "SAMPLE Legacy Tool"));
        var r = await app.Request(id);
        Assert.Equal(RequestStatus.Returned, r.Flow!.Status); // a failed check returns at once, even before the Entra check
        Assert.Contains("deprecated", r.Flow.LastReason);
        var returned = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.ReviewReturned);
        Assert.Contains(TestApp.SignIn(TestApp.Alice), returned.To);
        Assert.Contains("sample.pmo.alpha@sample.invalid", returned.To);
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested);

        var view = await app.View(id);
        Assert.Contains("Resubmit", view.Json.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        var put = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", TestApp.Intake(), view.Json.GetProperty("request").GetProperty("etag").GetString());
        Assert.Equal(200, put.Status);
        Assert.Equal(2, (await app.Request(id)).Revision);
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    [Fact]
    public async Task Rejection_by_owner_captures_remarks_notifies_requester_and_pmo_and_stops()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        Assert.Equal(400, (await app.Approve(id, TestApp.Olivia, "Rejected")).Status); // remarks required
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, "Rejected", "SAMPLE: not needed for this project.")).Status);
        var r = await app.Request(id);
        Assert.Equal(RequestStatus.Rejected, r.Flow!.Status);
        Assert.Equal(ResponseState.Rejected, (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value.State);
        var n = Assert.Single(await app.Notifications(id), x => x.Kind == NotificationKind.Rejected);
        Assert.Contains("not needed", n.Body);
        Assert.Contains("sample.pmo.alpha@sample.invalid", n.To);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Rejected, await app.Status(id)); // stop
    }

    [Fact]
    public async Task Rejection_by_security_stops_the_flow()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingSecurity);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam, "Rejected", "SAMPLE: access level too high for the risk.")).Status);
        Assert.Equal(RequestStatus.Rejected, await app.Status(id));
        Assert.Null((await app.Request(id)).Flow!.IdTeamTask);
    }
}
