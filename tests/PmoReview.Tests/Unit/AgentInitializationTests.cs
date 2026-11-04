using Microsoft.Extensions.Logging;
using PmoReview.Adapters;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Offboarding;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Re-check 6 (independent, 4 Nov 2026): how the AI agent starts. The worker's start-up checks and wiring
/// (<see cref="WorkerStartup"/>), settings that must refuse to start instead of being dropped, the website's wording against
/// what the worker can really do, switches turned off while a task waits for the agent, and one failing worker job.
/// </summary>
public class AgentInitializationTests
{
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string FinanceUser = "sample.e2001@sample.invalid";
    private const string FinanceGroup = "SAMPLE-GRP-ALPHA-FIN-VIEW";

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var d = values.ToDictionary(v => v.Key, v => v.Value);
        return k => d.GetValueOrDefault(k);
    }

    private static readonly (string, string)[] WorkerBase = [("ALLOW_PAID_SERVICES", "true"), ("COSMOS_ENDPOINT", "https://cosmos-sample.documents.azure.com:443/")];

    // ---- start-up checks ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_worker_refuses_to_start_without_paid_services_a_https_cosmos_endpoint_or_with_a_retired_switch()
    {
        var (settings, cosmos) = WorkerStartup.Check(Env(WorkerBase));
        Assert.Equal(("cosmos-sample.documents.azure.com", "Outbox"), (cosmos.Host, settings.NotifySender));

        Assert.Contains("ALLOW_PAID_SERVICES", Assert.Throws<InvalidOperationException>(() => WorkerStartup.Check(Env(("COSMOS_ENDPOINT", "https://c.documents.azure.com/")))).Message);
        Assert.Contains("COSMOS_ENDPOINT", Assert.Throws<InvalidOperationException>(() => WorkerStartup.Check(Env(("ALLOW_PAID_SERVICES", "true")))).Message);
        Assert.Contains("COSMOS_ENDPOINT", Assert.Throws<InvalidOperationException>(() =>
            WorkerStartup.Check(Env(("ALLOW_PAID_SERVICES", "true"), ("COSMOS_ENDPOINT", "http://c.documents.azure.com/")))).Message);
        foreach (var retired in WorkerStartup.RetiredSettings)
            Assert.Contains("retired", Assert.Throws<InvalidOperationException>(() => WorkerStartup.Check(Env([.. WorkerBase, (retired, "TRUE")]))).Message);
        // Off stays allowed (Bicep forces them to false).
        WorkerStartup.Check(Env([.. WorkerBase, ("ENTRA_AUTO_ASSIGN", "false")]));
    }

    [Fact]
    public void Settings_that_decide_what_the_agent_may_read_or_change_refuse_to_start_when_they_cannot_be_read()
    {
        // A domain name instead of the tenant id: the agent used to leave every real project alone, silently.
        Assert.Contains("GUID", Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(Env(("AGENT_TENANT_ID", "contoso.onmicrosoft.com")))).Message);
        Assert.Equal("5b9f9810-0512-49e5-b7f7-0159bc488570",
            PilotSettings.FromEnvironment(Env(("AGENT_TENANT_ID", " 5B9F9810-0512-49E5-B7F7-0159BC488570 "))).Workflow.AgentTenantId);
        // One digit short: the list used to become empty, which means every subscription the worker can list (wider).
        Assert.Contains("OFFBOARD_SUBSCRIPTIONS", Assert.Throws<InvalidOperationException>(() =>
            PilotSettings.FromEnvironment(Env(("OFFBOARD_SUBSCRIPTIONS", "5b9f9810-0512-49e5-b7f7-0159bc48857")))).Message);
        Assert.Equal(["5b9f9810-0512-49e5-b7f7-0159bc488570"],
            PilotSettings.FromEnvironment(Env(("OFFBOARD_SUBSCRIPTIONS", "5B9F9810-0512-49E5-B7F7-0159BC488570, 5b9f9810-0512-49e5-b7f7-0159bc488570"))).Workflow.OffboardSubscriptions);
        Assert.Empty(PilotSettings.FromEnvironment(_ => null).Workflow.OffboardSubscriptions);
    }

    [Theory]
    [InlineData(null, "Outbox", false)]
    [InlineData("", "Outbox", false)]
    [InlineData("Outbox", "Outbox", false)]
    [InlineData("AcsEmail", "AcsEmail", true)]
    [InlineData("GraphMail", "GraphMail", true)]
    public void Notify_sender_values_are_read_exactly(string? value, string sender, bool mail)
    {
        var s = PilotSettings.FromEnvironment(k => k == "NOTIFY_SENDER" ? value : null);
        Assert.Equal((sender, mail), (s.NotifySender, s.Workflow.MailEnabled));
    }

    [Theory]
    [InlineData("acsemail")]
    [InlineData("ACS")]
    [InlineData("Graph")]
    public void A_misspelt_notify_sender_refuses_to_start_instead_of_switching_mail_off(string value) =>
        Assert.Contains("NOTIFY_SENDER", Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => k == "NOTIFY_SENDER" ? value : null)).Message);

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("soon")]
    public void A_non_finite_sla_is_the_default_instead_of_a_start_up_crash(string value) =>
        Assert.Equal(TimeSpan.FromHours(24), PilotSettings.FromEnvironment(k => k == "OWNER_SLA_HOURS" ? value : null).Workflow.OwnerSla);

    // ---- what the worker holds ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]   // the live state on 31 Oct: AGENT_TAP on, no TAP_MAIL_SENDER
    [InlineData(true, true, false, false)]   // a sender, but the agent TAP off
    [InlineData(false, true, true, false)]   // no provisioning: the agent creates no account, so it never mails a pass
    [InlineData(true, true, true, true)]
    public void The_agent_mails_the_pass_only_with_provisioning_the_agent_tap_and_a_sender_mailbox(bool provisioning, bool tap, bool sender, bool mails)
    {
        var s = PilotSettings.FromEnvironment(Env([
            ("AGENT_PROVISIONING", provisioning ? "true" : "false"), ("AGENT_TAP", tap ? "true" : "false"), ("NOTIFY_SENDER", "AcsEmail"),
            .. sender ? new[] { ("TAP_MAIL_SENDER", "sample.tap.sender@sample.invalid") } : []]));
        Assert.Equal(mails, s.Workflow.TapMailByAgent);
        var can = WorkerStartup.Capabilities(s, _ => null);
        Assert.Equal((provisioning, mails, false), (can.DirectoryWriter, can.TapMail, can.RoleReader));
    }

    [Fact]
    public void Every_writer_exists_only_with_its_switch()
    {
        var none = WorkerStartup.Capabilities(PilotSettings.FromEnvironment(_ => null), _ => null);
        Assert.Equal(new WorkerStartup.WorkerCapabilities(false, false, false, false, false), none);
        var all = WorkerStartup.Capabilities(PilotSettings.FromEnvironment(Env(("AGENT_PROVISIONING", "true"), ("AGENT_ROLE_ASSIGNMENT", "true"), ("AGENT_TAP", "true"),
            ("TAP_MAIL_SENDER", "sample.tap.sender@sample.invalid"), ("AGENT_OFFBOARDING", "true"))), k => k == "ROLE_READER" ? "Arm" : null);
        Assert.Equal(new WorkerStartup.WorkerCapabilities(true, true, true, true, true), all);
    }

    // ---- the website says what the worker does --------------------------------------------------------------------------

    [Fact]
    public async Task With_the_agent_tap_on_but_no_sender_mailbox_the_site_says_the_id_team_issues_the_pass_and_the_agent_issues_none()
    {
        // The live settings on 31 Oct (AGENT_PROVISIONING and AGENT_TAP on, ACS mail, TAP_MAIL_SENDER not set).
        var live = PilotSettings.FromEnvironment(Env(("AGENT_PROVISIONING", "true"), ("AGENT_TAP", "true"), ("NOTIFY_SENDER", "AcsEmail")));
        var app = TestApp.With(settings: live);
        Assert.False((await app.Call(TestApp.Alice, "GET", "me")).Json.GetProperty("agentTap").GetBoolean());
        var who = Messages.WhoDoesWhatFor(live.Workflow);
        Assert.Contains("The ID team issues the Temporary Access Pass", who);
        Assert.DoesNotContain("mails the joiner", who);

        // The worker creates the account and leaves the pass to the ID team (it has no TAP mail).
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e2001@sample-corp.invalid", resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        await app.RunWorker();
        Assert.Contains($"create-user {FinanceUser}", app.Writer.Calls);
        Assert.DoesNotContain(app.Writer.Calls, c => c.StartsWith("create-tap", StringComparison.Ordinal));
        Assert.Empty(app.Mail.Sent);
        Assert.Contains("ONE-TIME Temporary Access Pass", (await app.Request(id)).Flow!.IdTeamTask!.Instructions);

        // With the sender mailbox the wording follows; the TAP mail does not use NOTIFY_SENDER, so Outbox does not change it.
        var withSender = live with { NotifySender = "Outbox", Workflow = live.Workflow with { MailEnabled = false, TapMailSender = "sample.tap.sender@sample.invalid" } };
        Assert.True((await TestApp.With(settings: withSender).Call(TestApp.Alice, "GET", "me")).Json.GetProperty("agentTap").GetBoolean());
        Assert.Contains("mails the joiner a one-time Temporary Access Pass", Messages.WhoDoesWhatFor(withSender.Workflow));
    }

    // ---- switches turned off while a task waits for the agent -----------------------------------------------------------

    private static PilotSettings Agent(bool provisioning, bool roles) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = provisioning, AgentRoleAssignment = roles,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [Scope],
        },
    };

    private static async Task<string> Approved(TestApp app)
    {
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e2001@sample-corp.invalid", resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        return id;
    }

    [Fact]
    public async Task Provisioning_switched_off_while_the_task_waits_for_the_agent_gives_the_id_team_the_whole_task_and_switched_on_again_the_agent_takes_it_back()
    {
        var on = TestApp.With(settings: Agent(provisioning: true, roles: false));
        var id = await Approved(on);
        var r = (await on.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.Agent), (r.Status, r.WaitingOn));
        Assert.DoesNotContain(await on.Notifications(id), n => n.Kind == NotificationKind.TeamTask);

        // AGENT_PROVISIONING=false (always allowed), before the worker ran: before re-check 6 the task stayed "waiting on the
        // AI agent", the ID team had no action and no task mail, and a day later got a reminder it could not act on.
        var off = on.Restart(Agent(provisioning: false, roles: false));
        await off.RunWorker();
        r = (await off.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Status, r.WaitingOn));
        Assert.Contains("switched off (AGENT_PROVISIONING=false)", r.IdTeamTask!.Agent!.Manual);
        Assert.Contains(r.Events, e => e.Step == "agent-manual");
        var mail = Assert.Single(await off.Notifications(id), n => n.Kind == NotificationKind.TeamTask);
        Assert.Contains("sample.idteam.alpha@sample.invalid", mail.To);
        Assert.NotEmpty((await off.View(id, TestApp.Ivan)).Json.GetProperty("actions").EnumerateArray());
        Assert.DoesNotContain(await off.Issues(id), i => i.Status == IssueStatus.Open); // a switch, not a fault
        Assert.Empty(off.Writer.Calls);

        // Switched on again before anyone in the ID team picked it up: the agent takes it back and runs every check.
        var again = off.Restart(Agent(provisioning: true, roles: false));
        await again.RunWorker();
        Assert.Equal([$"create-group {FinanceGroup}", $"create-user {FinanceUser}", $"add-member {FinanceUser} {FinanceGroup}"], again.Writer.Calls);
        r = (await again.Request(id)).Flow!;
        Assert.Contains(r.Events, e => e.Step == "agent-resumed" && e.Note.Contains("switched on again", StringComparison.Ordinal));
        Assert.True(r.IdTeamTask!.Agent is { Completed: true, Manual: null });
    }

    [Fact]
    public async Task A_task_the_id_team_picked_up_after_the_switch_off_stays_with_the_id_team_when_provisioning_is_switched_on_again()
    {
        var on = TestApp.With(settings: Agent(provisioning: true, roles: false));
        var id = await Approved(on);
        var off = on.Restart(Agent(provisioning: false, roles: false));
        await off.RunWorker();
        Assert.Equal(200, (await off.Call(TestApp.Ivan, "POST", $"projects/{TestApp.Alpha}/requests/{id}/tasks/id-team/pickup")).Status);

        var again = off.Restart(Agent(provisioning: true, roles: false));
        await again.RunWorker();
        Assert.Empty(again.Writer.Calls);
        Assert.Equal(WaitingOn.IdTeam, (await again.Request(id)).Flow!.WaitingOn);
    }

    [Fact]
    public async Task Role_assignment_switched_off_while_the_role_task_waits_for_the_agent_gives_the_role_team_the_task()
    {
        var app = TestApp.With(settings: Agent(provisioning: true, roles: true));
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            Role = "Reader", RoleScope = Scope, AzureRoleDefinitionId = FakeRoleWriter.Reader, AzureScope = Scope, Version = v.Value.Version + 1,
        }, v.ETag);
        app.Directory.AddUser(FinanceUser); // existing account: the agent records the ID step itself
        var id = await Approved(app);
        await app.RunWorker();
        app.RoleWriter.FailWith = "ArmUnavailable"; // the assignment waits for the next run
        await app.RunWorker();
        var r = (await app.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithRoleTeam, WaitingOn.Agent), (r.Status, r.WaitingOn));

        var off = app.Restart(Agent(provisioning: true, roles: false));
        await off.RunWorker();
        r = (await off.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithRoleTeam, WaitingOn.RoleTeam), (r.Status, r.WaitingOn));
        Assert.Contains("switched off (AGENT_ROLE_ASSIGNMENT=false)", r.RoleTeamTask!.Agent!.Manual);
        Assert.Contains(await off.Notifications(id), n => n.Kind == NotificationKind.TeamTask && n.To.Contains("sample.roleteam.alpha@sample.invalid"));
        Assert.NotEmpty((await off.View(id, TestApp.Rose)).Json.GetProperty("actions").EnumerateArray());
        Assert.Empty(off.RoleWriter.Calls);
    }

    // ---- one failing worker job -----------------------------------------------------------------------------------------

    private sealed class ThrowingReader : IOffboardingReader
    {
        public Task<IReadOnlyList<GroupMembership>> MembershipsAsync(string userObjectId, CancellationToken ct) => throw new InvalidOperationException("SAMPLE unexpected");
        public Task<IReadOnlyList<string>> TapMethodIdsAsync(string userObjectId, CancellationToken ct) => throw new InvalidOperationException("SAMPLE unexpected");
    }

    /// <summary>An Entra reader that fails with an error type no job expects (not a DirectoryReadException).</summary>
    private sealed class BrokenDirectory : IDirectoryReader
    {
        private static Exception E => new FormatException("SAMPLE malformed response");
        public Task<DirectoryUser?> FindUserAsync(string userPrincipalName, CancellationToken ct) => throw E;
        public Task<IReadOnlyList<DirectoryGroup>> FindGroupsAsync(string displayName, CancellationToken ct) => throw E;
        public Task<bool> IsMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct) => throw E;
        public Task<AuthMethods> GetAuthMethodsAsync(string userObjectId, CancellationToken ct) => throw E;
        public Task<SignInSummary?> LastSuccessfulSignInAsync(string userObjectId, DateTimeOffset since, CancellationToken ct) => throw E;
        public Task<string?> GetManagerAsync(string userPrincipalName, CancellationToken ct) => throw E;
    }

    private static WorkflowWorker Worker(TestApp app, GroupResolutionJob? groups, OffboardingJob? offboarding) => new(app.Worker.Agent,
        new NotificationDispatcher(app.Stores, new OutboxOnlySender(), app.Worker.Issues, app.Clock, app.Loggers.CreateLogger<NotificationDispatcher>()),
        new AdoSync(app.Stores, app.Tracker, app.Worker.Issues, app.Clock, app.Loggers.CreateLogger<AdoSync>()),
        new DailySummaryJob(app.Stores, app.Worker.Notifier, app.Clock), groups, offboarding, app.Stores, app.Worker.Issues, app.Loggers.CreateLogger<WorkflowWorker>());

    [Fact]
    public async Task A_job_that_fails_does_not_stop_the_reviews_and_mails_of_the_cycle_and_is_on_the_issues_list()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId();
        var worker = Worker(app, new GroupResolutionJob(app.Stores, new BrokenDirectory(), app.Clock, app.Loggers.CreateLogger<GroupResolutionJob>(), null,
            issues: app.Worker.Issues), null);

        // Merged 4 Nov 2026: group resolution handles an error per project (on the issues list; no App Insights) and the
        // worker reports any job that still throws on the issues list (WorkerIsolationTests), instead of failing the cycle.
        var summary = await worker.RunOnceAsync("sample-cycle", CancellationToken.None);
        Assert.Empty(summary.Failed);
        Assert.Contains(await app.Issues(null), i => i.Step == "group-resolution-error:FormatException");
        // Before re-check 6 the request was never reviewed and no mail moved while the group read kept failing.
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Contains(await app.Notifications(id), n => n.Kind == NotificationKind.ApprovalRequested && n.Status == NotificationStatus.Recorded);
    }

    [Fact]
    public async Task An_unexpected_error_in_one_offboarding_run_is_an_issue_and_the_rest_of_the_cycle_runs()
    {
        const string tenant = "00000000-0000-4000-8000-0000000000a1";
        const string upn = "sample.leaver@sample.invalid";
        var app = TestApp.With(settings: new PilotSettings { Workflow = new WorkflowSettings { AgentOffboarding = true, AgentTenantId = tenant } });
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = tenant, IsProductionTenant = false }, p.ETag);
        app.Directory.AddUser(upn);
        await app.Stores.PeopleInventory.CreateAsync(new Person
        {
            Id = Person.MakeId(upn), ProjectId = TestApp.Alpha, EnterpriseId = upn, EnterpriseEmail = upn, DisplayName = "SAMPLE Leaver", Domain = "Sales",
            UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
        Assert.Equal(202, (await app.Call(TestApp.Paul, "POST", $"offboarding/{Uri.EscapeDataString(upn)}?projectId={TestApp.Alpha}&mode=plan", new { reason = "Leaver" })).Status);
        var id = await app.SubmitId();
        var offboarding = new OffboardingJob(app.Stores, app.Directory, new ThrowingReader(), app.Offboarding, app.Offboarding, app.Offboarding, app.Worker.Notifier,
            app.Worker.Issues, app.Clock, app.Settings.Workflow, app.Loggers.CreateLogger<OffboardingJob>());

        await Worker(app, null, offboarding).RunOnceAsync("sample-cycle", CancellationToken.None);
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        var run = Assert.Single(await app.Stores.OffboardingRuns.ListAsync(TestApp.Alpha)).Value;
        Assert.Equal(OffboardingStatus.Planning, run.Status); // unchanged: tried again on the next run
        Assert.Contains(await app.Issues(null), i => i.Step == $"offboarding-error:{run.Id}:InvalidOperationException" && i.Summary.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Empty(app.Offboarding.Calls); // nothing written
    }
}
