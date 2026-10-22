using PmoReview.Adapters;
using PmoReview.Core.Domain;
using PmoReview.Core.Directory;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Agent privileged rows (Gokul, 22 Oct 2026; <c>AGENT_PRIVILEGED</c>, off until approved): after the named Owner and then
/// Security approve, the agent also provisions rows flagged privileged — the live case is a Data Engineer row with Storage
/// Blob Data Contributor, Key Vault Secrets Officer and Reader — with the same checks and allow-list. Privileged
/// administrator roles (Owner, Contributor, …) always stay with people, whatever the allow-list says.
/// </summary>
public class PrivilegedProvisioningTests
{
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string BlobContributor = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string SecretsOfficer = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7";
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string AdminRow = "A-DBW-ENG-ADMIN";
    private const string AdminGroup = "SAMPLE-GRP-ALPHA-DBW-ENG-ADMIN";
    private const string Joiner = "sample.e3001@sample.invalid";

    private static PilotSettings On(bool privileged, params string[] extraAllowed) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = true, AgentPrivileged = privileged,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader, BlobContributor, SecretsOfficer, .. extraAllowed], RoleAssignScopes = [Scope],
        },
    };

    /// <summary>The privileged SAMPLE row gets the Data Engineer row's Azure roles (as the importer writes them in test mode).</summary>
    private static async Task DataEngineerRoles(TestApp app, params string[] roleNames)
    {
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, AdminRow))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            AzureRoles = roleNames.Select(n => new AzureRoleGrant { RoleName = n, Scope = Scope, ResourceName = "SAMPLE storage and key vault" }).ToList(),
            Version = v.Value.Version + 1,
        }, v.ETag);
    }

    private static async Task<string> Approved(TestApp app)
    {
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e3001@sample-corp.invalid", accessLevel: "Admin",
            justification: "SAMPLE: data engineer who builds and deploys the ingestion pipelines, reads and writes the landing storage and rotates pipeline secrets."));
        await app.RunWorker();
        var reviewed = (await app.Request(id)).Flow!;
        Assert.True(reviewed.Status == RequestStatus.AwaitingOwner, reviewed.LastReason);
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        return id;
    }

    [Fact]
    public async Task With_privileged_rows_on_the_agent_provisions_a_privileged_row_and_assigns_its_allow_listed_roles_after_both_approvals()
    {
        var app = TestApp.With(settings: On(privileged: true));
        await DataEngineerRoles(app, "Storage Blob Data Contributor", "Key Vault Secrets Officer", "Reader");
        var id = await Approved(app);
        Assert.Empty(app.Writer.Calls); // the API holds no writer

        await app.RunWorker();
        Assert.Equal([$"create-user {Joiner}", $"add-member {Joiner} {AdminGroup}"], app.Writer.Calls);
        var r = (await app.Request(id)).Flow!;
        Assert.True(r.IdTeamTask!.Agent is { UserCreated: true, Membership: true, Completed: true, Manual: null });
        Assert.Contains(r.Events, e => e.Step == "agent-provisioned" && e.Note.Contains("Privileged access: done by the agent", StringComparison.Ordinal));

        // The agent TAP is off here: the ID team issues the pass, then the agent assigns all three roles.
        var now = app.Clock.UtcNow;
        app.Directory.SetTap(Joiner, new TapMethod(now, now, 60, true, true, "EnabledByPolicy"));
        Assert.Equal(200, (await app.ReportIdTeam(id, new
        {
            tap = new { issuedAt = now, expiresAt = now.AddMinutes(60), oneTime = true, issuedBy = TestApp.SignIn(TestApp.Ivan) },
            notes = "SAMPLE: TAP given to the user directly.",
        })).Status);
        await app.RunWorker();
        var group = app.Directory.GroupId(AdminGroup);
        Assert.Equal([$"Group {group} {BlobContributor} {Scope}", $"Group {group} {SecretsOfficer} {Scope}", $"Group {group} {FakeRoleWriter.Reader} {Scope}"],
            app.RoleWriter.Calls);
        r = (await app.Request(id)).Flow!;
        Assert.Equal(RequestStatus.Verifying, r.Status);
        Assert.Equal(WorkflowAgent.AgentName, r.RoleTeamTask!.ReportedBy);
        Assert.Contains("privileged access row", r.RoleTeamTask.RoleReport!.Notes);

        app.UserCompletesFirstSignIn(Joiner);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Completed, await app.Status(id));
    }

    [Fact]
    public async Task With_privileged_rows_off_the_task_stays_with_the_id_team_and_says_why()
    {
        var app = TestApp.With(settings: On(privileged: false));
        await DataEngineerRoles(app, "Storage Blob Data Contributor", "Reader");
        var id = await Approved(app);
        await app.RunWorker();

        Assert.Empty(app.Writer.Calls);
        var r = (await app.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Status, r.WaitingOn));
        Assert.Contains("is privileged access (agent privileged provisioning is off)", r.IdTeamTask!.Agent!.Manual);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Contributor")]
    [InlineData("User Access Administrator")]
    public async Task Privileged_administrator_roles_are_never_done_by_the_agent_even_when_allow_listed(string role)
    {
        var app = TestApp.With(settings: On(privileged: true, Owner, "b24988ac-6180-42a0-ab88-20f7382dd24c", "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9"));
        await DataEngineerRoles(app, role, "Reader");
        var id = await Approved(app);
        await app.RunWorker();
        await app.RunWorker();

        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        var r = (await app.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Status, r.WaitingOn));
        Assert.Contains($"includes {role}: privileged administrator roles are never handled by the agent", r.IdTeamTask!.Agent!.Manual);
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.Body.Contains("did not provision", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_owner_role_on_a_row_not_flagged_privileged_still_stays_with_people()
    {
        var app = TestApp.With(settings: On(privileged: false, Owner));
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with { AzureRoleDefinitionId = Owner, AzureScope = Scope, Version = v.Value.Version + 1 }, v.ETag);
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e3002@sample-corp.invalid", resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        await app.RunWorker();

        Assert.Empty(app.Writer.Calls); // a member of a group that holds Owner would hold Owner
        Assert.Contains("privileged administrator roles are never handled by the agent", (await app.Request(id)).Flow!.IdTeamTask!.Agent!.Manual);
    }

    [Theory]
    [InlineData("mapping row A-DBW-ENG-ADMIN is privileged access")] // wording before 22 Oct (the live Data Engineer request)
    [InlineData(null)]
    public async Task A_task_left_only_because_the_row_was_privileged_goes_back_to_the_agent_when_privileged_rows_are_switched_on(string? legacyWording)
    {
        // The plain SAMPLE privileged row: a restart re-seeds the SAMPLE rows, so the row is left as seeded.
        var off = TestApp.With(settings: On(privileged: false));
        var id = await Approved(off);
        await off.RunWorker();
        if (legacyWording is not null)
        {
            var v = (await off.Stores.Requests.GetAsync(TestApp.Alpha, id))!;
            var flow = v.Value.Flow!;
            await off.Stores.Requests.ReplaceAsync(v.Value with { Flow = flow with { IdTeamTask = flow.IdTeamTask! with { Agent = flow.IdTeamTask.Agent! with { Manual = legacyWording } } } }, v.ETag);
        }

        // Switched on: the worker restarts with the new settings on the same data and takes the task back.
        var on = TestApp.On(off.Stores, On(privileged: true));
        await on.RunWorker();
        Assert.Equal([$"create-user {Joiner}", $"add-member {Joiner} {AdminGroup}"], on.Writer.Calls);
        var r = (await on.Request(id)).Flow!;
        Assert.True(r.IdTeamTask!.Agent is { Completed: true, Manual: null });
        Assert.Contains(r.Events, e => e.Step == "agent-resumed");
        Assert.Contains("ONE-TIME Temporary Access Pass", r.IdTeamTask.Instructions); // the ID team now only issues the pass

        await on.RunWorker();
        Assert.Equal(2, on.Writer.Calls.Count); // idempotent
    }

    [Fact]
    public async Task A_privileged_task_the_id_team_already_picked_up_stays_with_the_id_team()
    {
        var off = TestApp.With(settings: On(privileged: false));
        var id = await Approved(off);
        await off.RunWorker();
        Assert.Equal(200, (await off.Call(TestApp.Ivan, "POST", $"projects/{TestApp.Alpha}/requests/{id}/tasks/id-team/pickup")).Status);

        var on = TestApp.On(off.Stores, On(privileged: true));
        await on.RunWorker();
        Assert.Empty(on.Writer.Calls);
        var r = (await on.Request(id)).Flow!;
        Assert.Equal(WaitingOn.IdTeam, r.WaitingOn);
        Assert.DoesNotContain(r.Events, e => e.Step == "agent-resumed");
    }

    [Fact]
    public void Privileged_rows_are_off_unless_switched_on_and_the_never_list_is_the_privileged_administrator_roles()
    {
        Assert.False(PilotSettings.FromEnvironment(_ => null).Workflow.AgentPrivileged);
        Assert.True(PilotSettings.FromEnvironment(k => k == "AGENT_PRIVILEGED" ? "true" : null).Workflow.AgentPrivileged);
        Assert.True(BuiltInRoles.IsNeverByAgent(new AzureRoleGrant { RoleName = "contributor" }));
        Assert.True(BuiltInRoles.IsNeverByAgent(new AzureRoleGrant { RoleName = "custom name", RoleDefinitionId = Owner }));
        Assert.False(BuiltInRoles.IsNeverByAgent(new AzureRoleGrant { RoleName = "Storage Blob Data Contributor" }));
        Assert.False(BuiltInRoles.IsNeverByAgent(new AzureRoleGrant { RoleName = "Key Vault Secrets Officer" }));
        var s = new WorkflowSettings { AgentProvisioning = true, AgentPrivileged = true };
        Assert.Contains("privileged administrator roles", Messages.WhoDoesWhatFor(s));
        Assert.DoesNotContain("privileged administrator roles", Messages.WhoDoesWhatFor(s with { AgentPrivileged = false }));
    }
}
