using PmoReview.Adapters;
using PmoReview.Core.Databricks;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.2 (Gokul, 2 Nov 2026; AGENT_DATABRICKS, off until approved): after both approvals the agent sets up a Databricks row's
/// group — account and workspace (A3), the entitlements and Unity Catalog privileges its name stands for (A4, A5) — tests
/// it live, and logs it in the trail, the checks and the audit log. Admin groups, unlisted workspaces or catalogs, and
/// rows that also carry Azure roles stay with the people.
/// </summary>
public class DatabricksAgentTests
{
    private const string Group = "SAMPLE-GRP-ALPHA-FIN-VIEW";
    private const string User = "sample.e2001@sample.invalid";
    private const string Scope = "dbx:ws-sample/sample_e2e.curated";
    private static readonly DatabricksWorkspace Ws = DatabricksWorkspace.Parse("ws-sample=https://adb-1234567890123456.7.azuredatabricks.net")!;

    private static PilotSettings Settings(bool databricks = true, string catalogs = "sample_e2e") => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = true, AgentDatabricks = databricks,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [TestApp.SampleAccessScope],
            DatabricksAccountId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", DatabricksWorkspaces = [Ws], DatabricksGrantCatalogs = [catalogs],
        },
    };

    /// <summary>PMO makes A-FIN-VIEW a Databricks row (and optionally renames its group or adds an Azure role).</summary>
    private static async Task DatabricksRow(TestApp app, string group = Group, bool azureRole = false)
    {
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            EntraGroup = group, Role = "Databricks workspace user", RoleScope = Scope,
            AzureRoleDefinitionId = azureRole ? FakeRoleWriter.Reader : null, AzureScope = azureRole ? TestApp.SampleAccessScope : null,
            AzureRoles = azureRole ? [new AzureRoleGrant { RoleName = "Reader", RoleDefinitionId = FakeRoleWriter.Reader, Scope = TestApp.SampleAccessScope }] : [],
            Version = v.Value.Version + 1,
        }, v.ETag);
    }

    /// <summary>Submitted, both approvals, the agent provisions, the ID team records the TAP: the role step is next.</summary>
    private static async Task<string> ToRoleStep(TestApp app)
    {
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e2001@sample-corp.invalid", resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, (await app.Request(id)).Flow!.Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Empty(app.Databricks.Calls); // nothing in Databricks before both approvals
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        Assert.Empty(app.Databricks.Calls);
        await app.RunWorker(); // user, group, membership
        var now = app.Clock.UtcNow;
        app.Directory.SetTap(User, new TapMethod(now, now, 60, true, true, "EnabledByPolicy"));
        Assert.Equal(200, (await app.ReportIdTeam(id, new
        {
            tap = new { issuedAt = now, expiresAt = now.AddMinutes(60), oneTime = true, issuedBy = TestApp.SignIn(TestApp.Ivan) },
            notes = "SAMPLE: TAP given to the user directly.",
        })).Status);
        return id;
    }

    [Fact]
    public async Task After_both_approvals_the_agent_sets_up_the_group_in_databricks_tests_it_and_logs_it()
    {
        var app = TestApp.With(settings: Settings());
        await DatabricksRow(app);
        var id = await ToRoleStep(app);
        await app.RunWorker(); // ID step verified, Databricks set up and tested, role step verified live, final: completed

        var r = await app.Request(id);
        Assert.Equal(RequestStatus.Completed, r.Flow!.Status);
        Assert.Equal(
        [
            $"account {Group}",
            $"assign ws-sample {Group} USER",
            $"entitlements ws-sample {Group} workspace-access,databricks-sql-access",
            $"grant ws-sample catalog sample_e2e {Group} USE_CATALOG",
            $"grant ws-sample schema sample_e2e.curated {Group} USE_SCHEMA,SELECT",
        ], app.Databricks.Calls.Where(c => !c.StartsWith("account", StringComparison.Ordinal) || c == $"account {Group}").Distinct().ToList());
        Assert.Empty(app.RoleWriter.Calls); // no Azure role on a Databricks row

        var task = r.Flow.RoleTeamTask!;
        Assert.Equal(WorkflowAgent.AgentName, task.ReportedBy);
        foreach (var code in new[] { DatabricksChecks.Workspace, DatabricksChecks.Entitlements, DatabricksChecks.Catalog, DatabricksChecks.Schema })
            Assert.Contains(task.Checks, c => c.Code == code && c.Status == CheckStatus.Pass); // re-tested live by the verification
        Assert.Contains("Reader", task.RoleReport!.Notes);
        Assert.Contains(r.Flow.Events, e => e.Step == "agent-databricks-setup" && e.Note.Contains("as Reader", StringComparison.Ordinal));
        Assert.Contains(r.Flow.Events, e => e.Step == "agent-databricks-tests" && e.Note.StartsWith("4 of 4 tests passed", StringComparison.Ordinal));
        var audit = Assert.Single((await app.Stores.AuditEvents.ListAsync(TestApp.Alpha)).Select(a => a.Value), a => a.Action == "databricks-setup");
        Assert.Equal(("Passed", $"group {Group}"), (audit.Result, audit.Target));
        Assert.Contains("DBX_UC_SCHEMA Pass", audit.Details);
        var record = (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value;
        Assert.Equal((ResponseState.Assigned, WorkflowAgent.AgentName), (record.State, record.AssignedBy));

        // Idempotent: another run changes nothing in Databricks.
        var writes = app.Databricks.Calls.Count(c => !c.StartsWith("account", StringComparison.Ordinal));
        await app.RunWorker();
        Assert.Equal(writes, app.Databricks.Calls.Count(c => !c.StartsWith("account", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Switched_off_the_role_team_sets_up_databricks_and_nothing_is_written()
    {
        var app = TestApp.With(settings: Settings(databricks: false));
        await DatabricksRow(app);
        var id = await ToRoleStep(app);
        await app.RunWorker();
        var task = (await app.Request(id)).Flow!.RoleTeamTask!;
        Assert.Contains("Databricks set-up is off", task.Agent!.Manual);
        Assert.Empty(app.Databricks.Calls);
        Assert.Equal(WaitingOn.RoleTeam, (await app.Request(id)).Flow!.WaitingOn);
    }

    [Fact]
    public async Task A_databricks_row_that_also_carries_azure_roles_is_left_to_the_people_and_never_half_completed()
    {
        var app = TestApp.With(settings: Settings());
        await DatabricksRow(app, azureRole: true);
        var id = await ToRoleStep(app);
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Contains("one row per plane", r.Flow!.RoleTeamTask!.Agent!.Manual);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.DoesNotContain(app.Databricks.Calls, c => !c.StartsWith("account", StringComparison.Ordinal));
        Assert.NotEqual(RequestStatus.Completed, r.Flow.Status);
    }

    [Theory]
    [InlineData("SAMPLE-GRP-ALPHA-DBX-ADMINS", "sample_e2e", "never given by the agent")]
    [InlineData(Group, "other_catalog", "DATABRICKS_GRANT_CATALOGS")]
    public async Task Admin_groups_and_catalogs_off_the_allow_list_stay_with_the_people(string group, string catalogs, string why)
    {
        var app = TestApp.With(settings: Settings(catalogs: catalogs));
        await DatabricksRow(app, group);
        var id = await ToRoleStep(app);
        await app.RunWorker();
        Assert.Contains(why, (await app.Request(id)).Flow!.RoleTeamTask!.Agent!.Manual);
        Assert.DoesNotContain(app.Databricks.Calls, c => !c.StartsWith("account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_set_up_that_does_not_read_back_goes_to_the_role_team_with_the_failed_tests_and_an_audit_entry()
    {
        var app = TestApp.With(settings: Settings());
        app.Databricks.DropGrants = true;
        await DatabricksRow(app);
        var id = await ToRoleStep(app);
        await app.RunWorker();

        var r = await app.Request(id);
        var task = r.Flow!.RoleTeamTask!;
        Assert.Contains("did not verify", task.Agent!.Manual);
        Assert.Contains(task.Checks, c => c.Code == DatabricksChecks.Schema && c.Status == CheckStatus.Fail);
        Assert.Contains(task.Checks, c => c.Code == DatabricksChecks.Entitlements && c.Status == CheckStatus.Pass);
        Assert.Equal(WaitingOn.RoleTeam, r.Flow.WaitingOn);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper && i.Severity == IssueSeverity.High);
        Assert.Contains((await app.Stores.AuditEvents.ListAsync(TestApp.Alpha)).Select(a => a.Value), a => a.Action == "databricks-setup" && a.Result == "Failed");
    }

    [Fact]
    public async Task The_verification_tests_a_role_team_report_live_in_databricks()
    {
        // The catalog is off the agent's list, so a person sets the group up; the verification still tests it in Databricks.
        var app = TestApp.With(settings: Settings(catalogs: "other_catalog"));
        await DatabricksRow(app);
        var id = await ToRoleStep(app);
        await app.RunWorker();
        Assert.Equal(200, (await app.ReportRoleTeam(id, TestApp.RoleReport("Databricks workspace user", Scope))).Status);
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal(TeamTaskState.Returned, r.Flow!.RoleTeamTask!.State); // nothing was done in Databricks
        Assert.Contains(r.Flow.RoleTeamTask.Checks, c => c.Code == DatabricksChecks.Workspace && c.Status == CheckStatus.Fail);

        // The person does it; reported again, the live tests pass and the request completes.
        app.Databricks.Assign(Ws, Group, "USER");
        app.Databricks.Entitle(Ws, Group, "workspace-access", "databricks-sql-access");
        app.Databricks.Grant(Ws, "catalog", "sample_e2e", Group, ["USE_CATALOG"]);
        app.Databricks.Grant(Ws, "schema", "sample_e2e.curated", Group, ["USE_SCHEMA", "SELECT"]);
        Assert.Equal(200, (await app.ReportRoleTeam(id, TestApp.RoleReport("Databricks workspace user", Scope))).Status);
        await app.RunWorker();
        r = await app.Request(id);
        Assert.Equal(RequestStatus.Completed, r.Flow!.Status);
        Assert.All(r.Flow.RoleTeamTask!.Checks.Where(c => c.Code.StartsWith("DBX_", StringComparison.Ordinal)), c => Assert.Equal(CheckStatus.Pass, c.Status));
    }

    [Fact]
    public async Task A_refused_databricks_write_waits_for_the_permission_like_the_other_agent_writes()
    {
        var app = TestApp.With(settings: Settings());
        app.Databricks.FailWith = "DatabricksForbidden";
        await DatabricksRow(app);
        var id = await ToRoleStep(app);
        await app.RunWorker();
        var task = (await app.Request(id)).Flow!.RoleTeamTask!;
        Assert.NotNull(task.Agent!.RefusedSince);
        Assert.Null(task.Agent.Manual);
    }
}
