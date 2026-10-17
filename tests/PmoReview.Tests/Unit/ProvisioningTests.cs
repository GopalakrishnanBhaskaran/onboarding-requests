using PmoReview.Adapters;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v2.1 agent provisioning (chosen 17 Oct 2026): after the named Owner and then Security approve, the worker creates the
/// user, group and membership and assigns an allow-listed Azure role; the ID team still issues the TAP. The API never
/// writes; nothing is written before both approvals; anything outside the safe path goes to the team.
/// </summary>
public class ProvisioningTests
{
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string FinanceUser = "sample.e2001@sample.invalid";
    private const string FinanceGroup = "SAMPLE-GRP-ALPHA-FIN-VIEW";

    private static TestApp App(bool roles = true) => TestApp.With(settings: new PilotSettings
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = roles,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [Scope],
        },
    });

    private static object Finance(string enterpriseId = "sample.e2001", bool guest = false) =>
        TestApp.Intake(enterpriseId: enterpriseId, resource: "SAMPLE Finance Reports", accessLevel: "Viewer", guest: guest);

    /// <summary>PMO gives the row an Azure role the agent may assign (Reader at the SAMPLE scope).</summary>
    private static async Task AzureRole(TestApp app, string rowId = "A-FIN-VIEW", string roleId = FakeRoleWriter.Reader)
    {
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, rowId))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            Role = "Reader", RoleScope = Scope, AzureRoleDefinitionId = roleId, AzureScope = Scope, Version = v.Value.Version + 1,
        }, v.ETag);
    }

    private static async Task<string> Approved(TestApp app, object intake)
    {
        var id = await app.SubmitId(intake);
        await app.RunWorker();
        var reviewed = (await app.Request(id)).Flow!;
        Assert.True(reviewed.Status == RequestStatus.AwaitingOwner, reviewed.LastReason);
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        return id;
    }

    [Fact]
    public async Task After_both_approvals_the_agent_creates_user_group_and_membership_assigns_the_role_and_the_id_team_only_issues_the_tap()
    {
        var app = App();
        await AzureRole(app);
        var id = await app.SubmitId(Finance());
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Empty(app.Writer.Calls); // nothing before both approvals
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);

        var r = await app.Request(id);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.Agent), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Empty(app.Writer.Calls); // the API holds no writer
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask);
        Assert.Empty((await app.View(id, TestApp.Ivan)).Json.GetProperty("actions").EnumerateArray()); // nothing for the ID team yet

        await app.RunWorker();
        Assert.Equal([$"create-user {FinanceUser}", $"create-group {FinanceGroup}", $"add-member {FinanceUser} {FinanceGroup}"], app.Writer.Calls);
        r = await app.Request(id);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        var task = r.Flow.IdTeamTask!;
        Assert.True(task.Agent is { UserCreated: true, GroupCreated: true, MemberAdded: true, Membership: true, MappingUpdated: true, Completed: true, Manual: null });
        Assert.Contains("ONE-TIME Temporary Access Pass", task.Instructions);
        Assert.Contains(r.Flow.Events, e => e.Step == "agent-provisioned");
        var row = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!.Value;
        Assert.Equal((GroupState.Exists, WorkflowState.AgentActor), (row.GroupState, row.UpdatedBy));
        Assert.DoesNotContain(await app.Issues(id), i => i.Kind == IssueKind.MappingChangedAfterReview); // its own sheet update: no re-review
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.Body.Contains("ONE-TIME", StringComparison.Ordinal));

        await app.RunWorker();
        Assert.Equal(3, app.Writer.Calls.Count); // idempotent: nothing created twice

        // The ID team issues the TAP in Entra and records its metadata only; the form carries no user/group facts.
        var now = app.Clock.UtcNow;
        app.Directory.SetTap(FinanceUser, new TapMethod(now, now, 60, true, true, "EnabledByPolicy"));
        Assert.Equal(200, (await app.ReportIdTeam(id, new
        {
            tap = new { issuedAt = now, expiresAt = now.AddMinutes(60), oneTime = true, issuedBy = TestApp.SignIn(TestApp.Ivan) },
            notes = "SAMPLE: TAP given to the user directly.",
        })).Status);
        var report = (await app.Request(id)).Flow!.IdTeamTask!.IdReport!;
        Assert.Equal((FinanceUser, FinanceGroup, true, true, true), (report.UserPrincipalName, report.GroupName, report.UserCreated, report.AddedToGroup, report.MappingSheetUpdated));

        await app.RunWorker(); // verifies the ID step, then assigns the role and verifies it
        r = await app.Request(id);
        Assert.Equal(RequestStatus.Verifying, r.Flow!.Status);
        Assert.Equal([$"Group {app.Directory.GroupId(FinanceGroup)} {FakeRoleWriter.Reader} {Scope}"], app.RoleWriter.Calls);
        Assert.Equal(WorkflowAgent.AgentName, r.Flow.RoleTeamTask!.ReportedBy);
        Assert.Contains(r.Flow.RoleTeamTask.Checks, c => c.Code == VerifyCodes.RoleInAzure && c.Status == CheckStatus.Pass);
        var record = (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value;
        Assert.Equal((ResponseState.Assigned, WorkflowAgent.AgentName), (record.State, record.AssignedBy));

        app.UserCompletesFirstSignIn(FinanceUser);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Completed, await app.Status(id));
        Assert.Single(app.RoleWriter.Calls);
    }

    [Fact]
    public async Task Existing_account_the_agent_records_the_id_step_itself_and_verifies_it_on_the_next_run()
    {
        var app = App(roles: false);
        app.Directory.AddUser("sample.e2002@sample.invalid");
        var id = await Approved(app, TestApp.Intake(enterpriseId: "sample.e2002"));

        await app.RunWorker();
        Assert.Equal([$"add-member sample.e2002@sample.invalid {TestApp.DefaultGroup}"], app.Writer.Calls);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal((TeamTaskState.Reported, WorkflowAgent.AgentName), (task.State, task.ReportedBy));
        Assert.Null(task.IdReport!.Tap); // existing account: no TAP

        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal((RequestStatus.WithRoleTeam, WaitingOn.RoleTeam), (r.Flow!.Status, r.Flow.WaitingOn)); // role assignment by the agent is off
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.To.Contains("sample.roleteam.alpha@sample.invalid"));
    }

    [Fact]
    public async Task Guest_and_privileged_requests_stay_with_the_id_team()
    {
        var app = App();
        var guest = await Approved(app, Finance("sample.e2003", guest: true));
        var privileged = await Approved(app, TestApp.Intake(enterpriseId: "sample.e2004", accessLevel: "Admin",
            justification: "SAMPLE: manages and configures the dev workspace and supports the data engineering team's deployments."));
        await app.RunWorker();

        Assert.Empty(app.Writer.Calls);
        foreach (var (id, reason) in new[] { (guest, "guest"), (privileged, "privileged") })
        {
            var r = await app.Request(id);
            Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn));
            Assert.Contains(reason, r.Flow.IdTeamTask!.Agent!.Manual);
            Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.Body.Contains("did not provision", StringComparison.Ordinal));
            Assert.Single((await app.View(id, TestApp.Ivan)).Json.GetProperty("actions").EnumerateArray());
        }
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("ambiguous")]
    public async Task Refused_write_or_ambiguous_group_goes_to_the_id_team_with_a_high_issue(string kind)
    {
        var app = App();
        var id = await Approved(app, TestApp.Intake(enterpriseId: "sample.e2005"));
        if (kind == "forbidden") app.Writer.FailWith = "GraphForbidden";
        else app.Directory.AddGroup(TestApp.DefaultGroup); // a second group with the reviewed name

        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal(WaitingOn.IdTeam, r.Flow!.WaitingOn);
        Assert.NotNull(r.Flow.IdTeamTask!.Agent!.Manual);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper && i.Severity == IssueSeverity.High && i.Status == IssueStatus.Open);
        Assert.False(app.Directory.IsMember(TestApp.DefaultGroup, "sample.e2005@sample.invalid"));
    }

    [Fact]
    public async Task Membership_not_replicated_yet_is_retried_on_the_next_run_without_creating_the_group_again()
    {
        var app = App(roles: false);
        app.Writer.NotReplicatedOnce = true;
        var id = await Approved(app, Finance("sample.e2006"));

        await app.RunWorker();
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.True(task.Agent is { GroupCreated: true, Completed: false } && task.Agent.GroupObjectId is not null);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.DependencyFailure && i.Status == IssueStatus.AutoFixed);

        await app.RunWorker();
        Assert.Single(app.Writer.Calls, c => c.StartsWith("create-group", StringComparison.Ordinal));
        Assert.Contains($"add-member sample.e2006@sample.invalid {FinanceGroup}", app.Writer.Calls);
        Assert.True((await app.Request(id)).Flow!.IdTeamTask!.Agent!.Completed);
    }

    [Theory]
    [InlineData(null, false)] // no Azure role on the row (e.g. a Databricks role): the role team, no issue
    [InlineData("8e3af657-a8ff-443c-a75c-2fe8c4bcb635", true)] // Owner: not on the allow-list
    public async Task Roles_outside_the_allow_list_or_without_an_azure_role_go_to_the_role_team(string? roleId, bool issue)
    {
        var app = App();
        if (roleId is not null) await AzureRole(app, roleId: roleId);
        app.Directory.AddUser("sample.e2007@sample.invalid");
        var id = await Approved(app, Finance("sample.e2007"));
        await app.RunWorker();
        await app.RunWorker();

        var r = await app.Request(id);
        Assert.Equal((RequestStatus.WithRoleTeam, WaitingOn.RoleTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Empty(app.RoleWriter.Calls);
        Assert.NotNull(r.Flow.RoleTeamTask!.Agent!.Manual);
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.Subject.StartsWith("Role assignment team", StringComparison.Ordinal));
        Assert.Equal(issue, (await app.Issues(id)).Any(i => i.Kind == IssueKind.NeedsDeveloper && i.Severity == IssueSeverity.High));
    }

    [Fact]
    public async Task Provisioning_off_keeps_the_v2_manual_flow()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        Assert.Equal(WaitingOn.IdTeam, (await app.Request(id)).Flow!.WaitingOn);
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        Assert.Null((await app.Request(id)).Flow!.IdTeamTask!.Agent);
    }

    [Fact]
    public async Task Pmo_sets_the_azure_role_on_a_mapping_row_only_as_a_guid_and_an_azure_scope_together()
    {
        var app = TestApp.Create();
        async Task<int> Put(string? roleId, string? scope)
        {
            var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
            var row = v.Value;
            return (await app.Call(TestApp.Paul, "PUT", $"projects/{TestApp.Alpha}/mapping-sheet/A-FIN-VIEW", new
            {
                resource = row.Resource, accessLevel = row.AccessLevel, entraGroup = row.EntraGroup, role = "Reader", roleScope = Scope,
                groupState = row.GroupState.ToString(), sourceReference = row.SourceReference, azureRoleDefinitionId = roleId, azureScope = scope,
            }, ifMatch: v.ETag)).Status;
        }
        Assert.Equal(400, await Put("not-a-guid", Scope));
        Assert.Equal(400, await Put(FakeRoleWriter.Reader, null));
        Assert.Equal(400, await Put(FakeRoleWriter.Reader, "SAMPLE dbw-alpha-dev"));
        Assert.Equal(200, await Put(FakeRoleWriter.Reader.ToUpperInvariant(), Scope));
        var saved = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!.Value;
        Assert.Equal((FakeRoleWriter.Reader, Scope), (saved.AzureRoleDefinitionId, saved.AzureScope));
    }

    [Fact]
    public async Task The_flow_overview_and_the_app_say_what_the_agent_does_in_this_deployment()
    {
        var app = App();
        Assert.True((await app.Call(TestApp.Alice, "GET", "me")).Json.GetProperty("agentProvisioning").GetBoolean());
        var id = await app.SubmitId(Finance());
        var overview = (await app.Notifications(id)).Single(n => n.Kind == NotificationKind.FlowOverview).Body;
        Assert.Contains("it never approves.", overview);
        Assert.Contains("Only after both approvals the AI agent creates the user ID, group and membership and assigns the approved Azure role", overview);
        Assert.Contains("The ID team issues the Temporary Access Pass", overview);
        Assert.DoesNotContain("never approves, creates or assigns", overview);

        var manual = TestApp.Create();
        Assert.False((await manual.Call(TestApp.Alice, "GET", "me")).Json.GetProperty("agentProvisioning").GetBoolean());
        var manualId = await manual.SubmitId();
        Assert.Contains("it never approves, creates or assigns", (await manual.Notifications(manualId)).Single(n => n.Kind == NotificationKind.FlowOverview).Body);
    }

    [Theory]
    [InlineData(Scope, true)]
    [InlineData(Scope + "/providers/Microsoft.Storage/storageAccounts/stsample", true)]
    [InlineData(Scope + "-other", false)]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000000", false)]
    public void Scope_allow_list_matches_whole_path_segments(string scope, bool allowed) =>
        Assert.Equal(allowed, WorkflowAgent.InScope(scope, Scope));
}
