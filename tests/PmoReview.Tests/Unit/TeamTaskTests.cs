using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Stages 4 and 5: the teams act, the agent verifies and returns the exact difference on a mismatch.</summary>
public class TeamTaskTests
{
    private static async Task<(TestApp App, string Id)> AtIdTeam()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        app.Directory.AddUser(TestApp.DefaultUser);
        app.Directory.AddMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        app.Directory.SetTap(TestApp.DefaultUser, new TapMethod(app.Clock.UtcNow, app.Clock.UtcNow, 60, true, true, "EnabledByPolicy"));
        return (app, id);
    }

    [Theory]
    [InlineData(false, 60, 0, true, "one-time use")]
    [InlineData(true, 480, 0, true, "shortest lifetime")]
    [InlineData(true, 60, -120, true, "before all approvals")]
    [InlineData(true, 60, 0, false, "only after the user is added to the group")]
    public async Task Tap_rules_are_enforced_on_the_recorded_metadata(bool oneTime, int lifetimeMinutes, int issuedOffsetMinutes, bool added, string message)
    {
        var (app, id) = await AtIdTeam();
        var issued = app.Clock.UtcNow.AddMinutes(issuedOffsetMinutes);
        var r = await app.ReportIdTeam(id, app.IdReport(added: added, tap: new { issuedAt = issued, expiresAt = issued.AddMinutes(lifetimeMinutes), oneTime, issuedBy = "sample.idteam@sample.invalid" }));
        Assert.Equal(400, r.Status);
        Assert.Contains(message, r.Json.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(TeamTaskState.Open, (await app.Request(id)).Flow!.IdTeamTask!.State);
    }

    [Fact]
    public async Task Tap_recorded_to_the_minute_of_the_security_approval_counts_as_after_it()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingSecurity);
        app.Clock.Advance(TimeSpan.FromSeconds(42));
        var approve = await app.Approve(id, TestApp.Sam);
        Assert.Equal(200, approve.Status);
        var approvedAt = (await app.Request(id)).Flow!.CurrentApproval(ApprovalStage.Security)!.At;
        Assert.NotEqual(0, approvedAt.Second);
        app.Directory.AddUser(TestApp.DefaultUser);
        app.Directory.AddMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        var issued = new DateTimeOffset(approvedAt.Year, approvedAt.Month, approvedAt.Day, approvedAt.Hour, approvedAt.Minute, 0, approvedAt.Offset);

        var r = await app.ReportIdTeam(id, app.IdReport(tap: new { issuedAt = issued, expiresAt = issued.AddMinutes(60), oneTime = true, issuedBy = "sample.idteam@sample.invalid" }));

        Assert.True(r.Status == 200, r.Body);
    }

    [Fact]
    public async Task New_account_needs_a_one_time_tap_recorded()
    {
        var (app, id) = await AtIdTeam();
        var r = await app.ReportIdTeam(id, app.IdReport(includeTap: false));
        Assert.Equal(400, r.Status);
        Assert.Contains("never the pass", r.Json.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("""{"userPrincipalName":"sample.e1001@sample.invalid","groupName":"SAMPLE-GRP-ALPHA-DBW-ENG-USER","addedToGroup":true,"temporaryAccessPass":"Abc-123"}""")]
    [InlineData("""{"userPrincipalName":"sample.e1001@sample.invalid","groupName":"SAMPLE-GRP-ALPHA-DBW-ENG-USER","addedToGroup":true,"tap":{"issuedAt":"2026-10-17T09:00:00Z","expiresAt":"2026-10-17T10:00:00Z","oneTime":true,"value":"Abc-123"}}""")]
    [InlineData("""{"userPrincipalName":"sample.e1001@sample.invalid","groupName":"SAMPLE-GRP-ALPHA-DBW-ENG-USER","addedToGroup":true,"password":"Abc-123"}""")]
    public async Task The_pass_itself_is_never_accepted(string body)
    {
        var (app, id) = await AtIdTeam();
        var r = await app.Call(TestApp.Ivan, "POST", $"projects/{TestApp.Alpha}/requests/{id}/tasks/id-team/report", rawBody: body);
        Assert.Equal(400, r.Status);
        Assert.Contains("Never send the Temporary Access Pass", r.Body);
        Assert.DoesNotContain("Abc-123", r.Body);
    }

    [Fact]
    public async Task Only_tap_metadata_is_stored_and_notes_are_masked()
    {
        var (app, id) = await AtIdTeam();
        var body = new
        {
            userPrincipalName = TestApp.DefaultUser, groupName = TestApp.DefaultGroup, userCreated = true, addedToGroup = true,
            tap = new { issuedAt = app.Clock.UtcNow, expiresAt = app.Clock.UtcNow.AddMinutes(30), oneTime = true, issuedBy = "sample.idteam@sample.invalid" },
            notes = "SAMPLE: TAP is Zz9-Pass-Value given by phone",
        };
        Assert.Equal(200, (await app.ReportIdTeam(id, body)).Status);
        var report = (await app.Request(id)).Flow!.IdTeamTask!.IdReport!;
        Assert.Equal(new TapRecord(app.Clock.UtcNow, app.Clock.UtcNow.AddMinutes(30), true, "sample.idteam@sample.invalid", app.Clock.UtcNow), report.Tap);
        Assert.DoesNotContain("Zz9-Pass-Value", report.Notes);
    }

    [Fact]
    public async Task Different_group_name_is_returned_to_the_id_team_with_the_exact_difference()
    {
        var (app, id) = await AtIdTeam();
        app.Directory.AddGroup("SAMPLE-GRP-ALPHA-DBW-ENG-USERS");
        app.Directory.AddMember("SAMPLE-GRP-ALPHA-DBW-ENG-USERS", TestApp.DefaultUser);
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport(group: "SAMPLE-GRP-ALPHA-DBW-ENG-USERS"))).Status);
        await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.WithIdTeam, flow.Status);
        Assert.Equal(WaitingOn.IdTeam, flow.WaitingOn);
        Assert.Equal(TeamTaskState.Returned, flow.IdTeamTask!.State);
        Assert.Contains($"Expected group {TestApp.DefaultGroup}", flow.IdTeamTask.LastDifference);
        Assert.Contains("update the mapping sheet", flow.IdTeamTask.LastDifference);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.GroupNameMismatch);
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TaskReturned && n.Body.Contains("Expected group"));

        // Fixed: report again with the reviewed group -> verified.
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport())).Status);
        await app.RunWorker();
        Assert.Equal(RequestStatus.WithRoleTeam, await app.Status(id));
        Assert.All((await app.Issues(id)).Where(i => i.Kind == IssueKind.GroupNameMismatch), i => Assert.Equal(IssueStatus.Resolved, i.Status));
    }

    [Fact]
    public async Task Missing_membership_in_entra_is_returned()
    {
        var (app, id) = await AtIdTeam();
        app.Directory.RemoveMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        await app.ReportIdTeam(id, app.IdReport());
        await app.RunWorker();
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal(TeamTaskState.Returned, task.State);
        Assert.Contains("is not a member of", task.LastDifference);
    }

    [Fact]
    public async Task Entra_tap_that_is_not_one_time_is_returned()
    {
        var (app, id) = await AtIdTeam();
        app.Directory.SetTap(TestApp.DefaultUser, new TapMethod(app.Clock.UtcNow, app.Clock.UtcNow, 60, false, true, "EnabledByPolicy"));
        await app.ReportIdTeam(id, app.IdReport());
        await app.RunWorker();
        Assert.Contains("not one-time", (await app.Request(id)).Flow!.IdTeamTask!.LastDifference);
    }

    [Fact]
    public async Task Group_to_be_created_needs_the_mapping_sheet_updated_and_the_update_triggers_a_new_review()
    {
        var app = TestApp.Create();
        var body = TestApp.Intake(resource: "SAMPLE Finance Reports", accessLevel: "Viewer", justification: "SAMPLE: reads finance reports for the monthly close.");
        var id = await app.SubmitId(body);
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        Assert.Contains("create it", (await app.Request(id)).Flow!.IdTeamTask!.Instructions);

        // ID team creates the group and the user, but forgets the mapping sheet.
        app.Directory.AddGroup("SAMPLE-GRP-ALPHA-FIN-VIEW");
        app.Directory.AddUser(TestApp.DefaultUser);
        app.Directory.AddMember("SAMPLE-GRP-ALPHA-FIN-VIEW", TestApp.DefaultUser);
        await app.ReportIdTeam(id, app.IdReport(group: "SAMPLE-GRP-ALPHA-FIN-VIEW"));
        await app.RunWorker();
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal(TeamTaskState.Returned, task.State);
        Assert.Contains("still says group SAMPLE-GRP-ALPHA-FIN-VIEW is 'to be created'", task.LastDifference);

        // ID team updates the mapping sheet through the app (group state only) and reports again.
        var sheet = await app.Call(TestApp.Ivan, "GET", $"projects/{TestApp.Alpha}/mapping-sheet");
        var etag = sheet.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("value").GetProperty("rowId").GetString() == "A-FIN-VIEW").GetProperty("etag").GetString();
        var upd = await app.Call(TestApp.Ivan, "POST", $"projects/{TestApp.Alpha}/mapping-sheet/A-FIN-VIEW/group", new { groupState = "Exists" }, etag);
        Assert.Equal(200, upd.Status);
        await app.ReportIdTeam(id, app.IdReport(group: "SAMPLE-GRP-ALPHA-FIN-VIEW"));
        await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        // The sheet changed after the review: reviewed again; same group and role, so it continued without new approvals.
        Assert.Contains(flow.Events, e => e.Step == "mapping-changed");
        Assert.Equal(RequestStatus.WithRoleTeam, flow.Status);
        Assert.Equal(2, flow.Approvals.Count(a => !a.Superseded));
    }

    [Fact]
    public async Task Changing_the_group_on_the_sheet_after_approval_needs_new_approvals()
    {
        var (app, id) = await AtIdTeam();
        var row = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-DBW-ENG-USER"))!;
        app.Directory.AddGroup("SAMPLE-GRP-ALPHA-DBW-ENG-USERS");
        await app.Stores.MappingSheet.ReplaceAsync(row.Value with { EntraGroup = "SAMPLE-GRP-ALPHA-DBW-ENG-USERS", Version = 2 }, row.ETag);
        await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.AwaitingOwner, flow.Status);
        Assert.All(flow.Approvals, a => Assert.True(a.Superseded));
        Assert.Equal(TeamTaskState.Cancelled, flow.IdTeamTask!.State);
        Assert.Equal("SAMPLE-GRP-ALPHA-DBW-ENG-USERS", flow.Scope!.EntraGroup);
        var record = (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value;
        Assert.Equal("SAMPLE-GRP-ALPHA-DBW-ENG-USERS", record.EntraGroup);
        Assert.Null(record.OwnerApprovedBy);
    }

    [Fact]
    public async Task Extra_role_is_returned_to_the_role_team_to_correct_or_revoke()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithRoleTeam);
        var body = new { assignedRoles = new[] { new { role = TestApp.DefaultRole, scope = TestApp.DefaultScope }, new { role = "SAMPLE Workspace Admin", scope = TestApp.DefaultScope } } };
        Assert.Equal(200, (await app.ReportRoleTeam(id, body)).Status);
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.WithRoleTeam, flow.Status);
        Assert.Equal(TeamTaskState.Returned, flow.RoleTeamTask!.State);
        Assert.Contains("extra SAMPLE Workspace Admin", flow.RoleTeamTask.LastDifference);
        Assert.Contains("revoke", flow.RoleTeamTask.LastDifference);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.RoleMismatch);
    }

    [Fact]
    public async Task Wrong_role_in_azure_is_caught_by_the_role_reader()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithRoleTeam);
        app.Roles.Assign(app.Directory.GroupId(TestApp.DefaultGroup), "SAMPLE Workspace Admin", TestApp.DefaultScope);
        // The team reports the right role, but the worker reads Azure: the assigned role differs.
        var view = await app.Worker.Service.ReportRoleTeamAsync(
            new PmoReview.Core.Security.CallerContext(TestApp.Rose, TestApp.SignIn(TestApp.Rose),
                new Dictionary<string, IReadOnlySet<AppRole>> { [TestApp.Alpha] = new HashSet<AppRole> { AppRole.RoleTeam } }),
            TestApp.Alpha, id, new RoleTeamReportInput([new AssignedRoleInput(TestApp.DefaultRole, TestApp.DefaultScope)], null), "cid", CancellationToken.None);
        var task = (await app.Request(id)).Flow!.RoleTeamTask!;
        Assert.Equal(TeamTaskState.Returned, task.State);
        Assert.Contains("Assigned in Azure", task.LastDifference);
        Assert.Contains("missing SAMPLE Workspace User", task.LastDifference);
    }

    [Fact]
    public async Task Team_actions_need_the_team_role()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        Assert.Equal(403, (await app.ReportIdTeam(id, app.IdReport(), user: TestApp.Rose)).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests/{id}/tasks/id-team/pickup")).Status);
        Assert.Equal(409, (await app.ReportRoleTeam(id, TestApp.RoleReport())).Status); // no open role task yet
    }

    [Fact]
    public async Task Pick_up_records_who_and_stops_the_sla_clock()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        Assert.Equal(200, (await app.Call(TestApp.Ivan, "POST", $"projects/{TestApp.Alpha}/requests/{id}/tasks/id-team/pickup")).Status);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal(TestApp.SignIn(TestApp.Ivan), task.PickedUpBy);
        app.Clock.Advance(TimeSpan.FromHours(60));
        await app.RunWorker();
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind is NotificationKind.Reminder or NotificationKind.Escalation);
    }

    [Fact]
    public async Task Final_verification_waits_and_asks_the_id_team_to_delete_a_leftover_tap()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.Verifying);
        app.Directory.SignInNow(TestApp.DefaultUser, app.Clock.UtcNow);
        app.Directory.SetMethods(TestApp.DefaultUser, ["password", "microsoftAuthenticator", "temporaryAccessPass"]);
        await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.Verifying, flow.Status);
        Assert.Contains(flow.FinalChecks, c => c.Code == VerifyCodes.FinalTapDeleted && c.Status == CheckStatus.Pending);
        Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.TapCleanupNeeded);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.TapNotDeleted);

        app.Directory.SetTap(TestApp.DefaultUser, null);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Completed, await app.Status(id));
    }

    [Fact]
    public async Task Access_removed_before_completion_goes_back_to_the_id_team()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.Verifying);
        app.Directory.RemoveMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        await app.RunWorker();
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.WithIdTeam, flow.Status);
        Assert.Equal(TeamTaskState.Returned, flow.IdTeamTask!.State);

        app.Directory.AddMember(TestApp.DefaultGroup, TestApp.DefaultUser);
        await app.ReportIdTeam(id, app.IdReport());
        await app.RunWorker();
        Assert.Equal(RequestStatus.Verifying, await app.Status(id)); // roles already verified: straight back to final verification
    }
}
