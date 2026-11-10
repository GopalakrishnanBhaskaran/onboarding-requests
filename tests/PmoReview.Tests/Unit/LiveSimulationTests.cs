using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Live simulations (Gokul, 7 Nov 2026): the developer approves every stage of a simulation and the AI agent provisions,
/// assigns and verifies it in Entra and Azure for real. Only with SIMULATION_LIVE_WRITES, never in production, only for a
/// project that names the agent's tenant and is marked not production, never for a Prod environment or a guest. The rule is
/// checked at submit and again before every agent write; the access is in the response document so Offboard user removes it.
/// </summary>
public class LiveSimulationTests
{
    private const string Dev = "sample-user-dev"; // Developer in SAMPLE-ALPHA (sample data)
    private const string Tenant = "00000000-0000-4000-8000-0000000000a1";
    private const string OtherTenant = "00000000-0000-4000-8000-0000000000b2";
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string FinanceUser = "sample.e2001@sample.invalid";
    private const string FinanceGroup = "SAMPLE-GRP-ALPHA-FIN-VIEW";

    private static PilotSettings Settings(bool live = true, string? redirect = null, bool tap = false, bool liveTap = false) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = true, AgentOffboarding = true,
            AgentTap = tap, TapMailSender = tap ? "sample.tap.sender@sample.invalid" : null,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [Scope],
            AgentTenantId = Tenant, SimulationLiveWrites = live, SimulationLiveTap = liveTap, MailRedirectTo = redirect, AppEnvironment = "test",
        },
    };

    /// <summary>SAMPLE-ALPHA named as the agent's tenant, marked not production (or as given), and the Finance row with an allow-listed role.</summary>
    private static async Task<TestApp> App(bool live = true, bool? production = false, string? redirect = null, bool tap = false, bool liveTap = false)
    {
        var app = TestApp.With(settings: Settings(live, redirect, tap, liveTap));
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = Tenant, IsProductionTenant = production }, p.ETag);
        // The other sample projects have no tenant id, which counts as the agent's own tenant (Tenants.Connected): give them their
        // own tenant, as real projects in other tenants have (review 10 Nov 2026, finding S1).
        foreach (var other in await app.Stores.Projects.ListAllAsync())
            if (other.Value.ProjectId != TestApp.Alpha && string.IsNullOrWhiteSpace(other.Value.TenantId))
                await app.Stores.Projects.ReplaceAsync(other.Value with { TenantId = OtherTenant }, other.ETag);
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            Role = "Reader", RoleScope = Scope, AzureRoleDefinitionId = FakeRoleWriter.Reader, AzureScope = Scope, Version = v.Value.Version + 1,
        }, v.ETag);
        return app;
    }

    private static object Finance(bool guest = false, string? environment = null, string enterpriseId = "sample.e2001@sample-corp.invalid") =>
        TestApp.Intake(enterpriseId: enterpriseId, resource: "SAMPLE Finance Reports", accessLevel: "Viewer", guest: guest, environment: environment);

    private static async Task<string> Simulate(TestApp app, object intake)
    {
        var sim = await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/simulate", intake);
        Assert.True(sim.Status == 201, sim.Body);
        return sim.Request.GetProperty("id").GetString()!;
    }

    private static async Task<string> SimulatedAndApproved(TestApp app, object intake)
    {
        var id = await Simulate(app, intake);
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Equal(200, (await app.Approve(id, Dev)).Status); // the developer as Owner
        Assert.Equal(200, (await app.Approve(id, Dev)).Status); // and as Security
        return id;
    }

    [Fact]
    public async Task A_live_simulation_is_provisioned_assigned_and_verified_for_real_and_offboarding_can_remove_it()
    {
        var app = await App();
        var id = await SimulatedAndApproved(app, Finance());
        var r = await app.Request(id);
        Assert.True(r is { Simulated: true, LiveWrites: true, DryRun: false });
        Assert.Contains(r.Flow!.Events, e => e.Step == "simulation" && e.Note!.StartsWith("LIVE SIMULATION", StringComparison.Ordinal));
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.Agent), (r.Flow.Status, r.Flow.WaitingOn)); // the agent, as for a real request

        await app.RunWorker();
        Assert.Equal([$"create-group {FinanceGroup}", $"create-user {FinanceUser}", $"add-member {FinanceUser} {FinanceGroup}"], app.Writer.Calls);
        Assert.NotNull(app.Directory.User(FinanceUser)); // really created (in the fake Entra)

        // The developer stands in for the ID team: issues the one-time TAP in Entra and records its metadata only.
        var now = app.Clock.UtcNow;
        app.Directory.SetTap(FinanceUser, new TapMethod(now, now, 60, true, true, "EnabledByPolicy"));
        Assert.Equal(200, (await app.ReportIdTeam(id, new
        {
            tap = new { issuedAt = now, expiresAt = now.AddMinutes(60), oneTime = true, issuedBy = "sample.dev@sample.invalid" },
            notes = "SAMPLE: live simulation.",
        }, Dev)).Status);
        await app.RunWorker(); // verifies the ID step in Entra, assigns the role, verifies everything: completed

        r = await app.Request(id);
        Assert.Equal(RequestStatus.Completed, r.Flow!.Status);
        Assert.Equal([$"Group {app.Directory.GroupId(FinanceGroup)} {FakeRoleWriter.Reader} {Scope}"], app.RoleWriter.Calls);
        Assert.DoesNotContain(r.Flow.FinalChecks!, c => c.Code == VerifyCodes.Simulated); // real checks, not "not checked"
        Assert.Contains(r.Flow.RoleTeamTask!.Checks, c => c.Code == VerifyCodes.RoleInAzure && c.Status == CheckStatus.Pass);
        Assert.Equal(ResponseState.Assigned, (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value.State);
        var all = (await app.Stores.Requests.ListAsync(TestApp.Alpha)).Select(x => x.Value).ToList();
        Assert.Equal(0, Reports.Daily(TestApp.Alpha, DateOnly.FromDateTime(r.CreatedAt.UtcDateTime), all, [], app.Clock.UtcNow).Submitted); // still not counted

        // Clean-up: the person it created is offboardable (a dry-run simulation never is).
        var plan = await app.Call(TestApp.Paul, "POST", $"offboarding/{Uri.EscapeDataString(FinanceUser)}?projectId={TestApp.Alpha}&mode=plan", new { reason = "Leaver" });
        Assert.True(plan.Status == 202, plan.Body);

        // Review finding 5: deleting it first would leave access nothing can offboard: a reason, and offboard first.
        Assert.Equal(400, (await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/{id}/delete", new { confirm = true })).Status);
        var deleted = await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/{id}/delete", new { confirm = true, reason = "SAMPLE: live test done" });
        Assert.Equal((409, "offboard_first"), (deleted.Status, deleted.ErrorCode));
        Assert.NotNull(await app.Stores.Requests.GetAsync(TestApp.Alpha, id));
    }

    [Fact]
    public async Task A_live_simulation_is_mailed_only_where_every_mail_goes_to_the_test_mailbox()
    {
        foreach (var redirect in new string?[] { "sample.tests@sample.invalid", null })
        {
            var app = await App(redirect: redirect);
            var id = await Simulate(app, Finance());
            await app.RunWorker(); // the test worker records every mail (outbox only): put this request's back in the queue
            foreach (var n in await app.Stores.Notifications.ListAsync(TestApp.Alpha))
                if (n.Value.RequestId == id && n.Value.To.Count > 0)
                    await app.Stores.Notifications.ReplaceAsync(n.Value with { Status = NotificationStatus.Pending, SentAt = null }, n.ETag);
            Assert.Contains(await app.Notifications(id), n => n.Status == NotificationStatus.Pending);
            var sender = new CapturingSender();
            await new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance, redirectTo: redirect)
                .RunOnceAsync(CancellationToken.None);
            if (redirect is null) Assert.DoesNotContain(sender.Sent, n => n.RequestId == id);
            else Assert.Contains(sender.Sent, n => n.RequestId == id && n.To.SequenceEqual([redirect]));
        }
    }

    public static TheoryData<string, bool, bool?, bool, string?> NotLive => new()
    {
        { "switched off", false, false, false, null },
        { "tenant not marked (counts as production)", true, null, false, null },
        { "production tenant", true, true, false, null },
        { "guest", true, false, true, null },
    };

    [Theory]
    [MemberData(nameof(NotLive))]
    public async Task Otherwise_a_simulation_stays_a_dry_run_and_nothing_is_written(string why, bool live, bool? production, bool guest, string? environment)
    {
        var app = await App(live, production);
        if (environment is not null)
        {
            var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
            await app.Stores.Projects.ReplaceAsync(p.Value with { Environments = ["Dev", "Prod"] }, p.ETag);
        }
        var id = await Simulate(app, Finance(guest, environment));
        var r = await app.Request(id);
        Assert.True(r is { Simulated: true, LiveWrites: false, DryRun: true }, why);
        Assert.Contains(r.Flow!.Events, e => e.Step == "simulation" && e.Note!.StartsWith("SIMULATION", StringComparison.Ordinal));
        await app.RunWorker();
        if (await app.Status(id) == RequestStatus.AwaitingOwner)
        {
            Assert.Equal(200, (await app.Approve(id, Dev)).Status);
            Assert.Equal(200, (await app.Approve(id, Dev)).Status);
            await app.RunWorker();
        }
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.Null(await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id));
    }

    [Fact]
    public async Task Without_the_live_simulation_tap_switch_the_agent_never_issues_or_mails_a_tap_for_a_live_simulation()
    {
        var app = await App(tap: true, redirect: "sample.tests@sample.invalid"); // finding 1: the sign-in mail is never redirected
        var id = await SimulatedAndApproved(app, Finance());
        await app.RunWorker();
        Assert.Contains($"create-user {FinanceUser}", app.Writer.Calls); // the account is created for real
        Assert.DoesNotContain(app.Writer.Calls, c => c.StartsWith("create-tap", StringComparison.Ordinal));
        Assert.Empty(app.Mail.Sent);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Null(task.Agent!.SignInDetailsSentAt);
        Assert.Contains("ONE-TIME Temporary Access Pass", task.Instructions); // the developer, as the ID team, issues it
    }

    [Fact]
    public async Task With_the_live_simulation_tap_switch_the_agent_mails_a_one_time_tap_to_the_enterprise_id_never_redirected()
    {
        // Gokul, 7 Nov 2026: "temp pass also not generated. create that also and send to the email that is marked as enterprise id".
        var app = await App(tap: true, liveTap: true, redirect: "sample.tests@sample.invalid");
        var id = await SimulatedAndApproved(app, Finance());
        await app.RunWorker();
        Assert.Contains($"create-user {FinanceUser}", app.Writer.Calls);
        Assert.Single(app.Writer.Calls, c => c.StartsWith("create-tap", StringComparison.Ordinal));
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal(["sample.e2001@sample-corp.invalid"], mail.To); // the Enterprise ID, not the test mailbox
        Assert.StartsWith("[LIVE SIMULATION] ", mail.Subject);
        Assert.Contains("LIVE SIMULATION (test account)", mail.Body);
        Assert.Contains("LIVE SIMULATION (test account)", mail.Html);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.NotNull(task.Agent!.SignInDetailsSentAt);
        Assert.Equal(TapRecipient.FromLiveSimulationSource, task.Agent.SignInDetailsFrom);
        Assert.Equal("s***@sample-corp.invalid", task.Agent.SignInDetailsSentTo);
    }

    [Fact]
    public async Task A_live_simulation_may_mail_the_tap_to_a_personal_mailbox_alias_a_real_request_may_not()
    {
        var app = await App(tap: true, liveTap: true);
        var id = await SimulatedAndApproved(app, Finance(enterpriseId: "sample.tester+e2001@gmail.com"));
        await app.RunWorker();
        Assert.Equal(["sample.tester+e2001@gmail.com"], Assert.Single(app.Mail.Sent).To);

        var r = await app.Request(id);
        Assert.Contains("personal mail domain", TapRecipient.FromApprovedRequest(r with { Simulated = false, LiveWrites = false }, null, r.DomainId!).Reason);
        Assert.Contains("personal mail domain", TapRecipient.FromApprovedRequest(r with { LiveWrites = false }, null, r.DomainId!).Reason); // a dry run
    }

    [Fact]
    public async Task A_dry_run_task_left_waiting_on_the_agent_goes_to_the_developer_at_the_next_run()
    {
        // Live report 7 Nov 2026: "Waiting on AI agent" for 1 h 15 m on a dry-run simulation's open ID team task.
        var app = await App(live: false);
        var id = await SimulatedAndApproved(app, Finance());
        var stored = (await app.Stores.Requests.GetAsync(TestApp.Alpha, id))!;
        Assert.Equal(RequestStatus.WithIdTeam, stored.Value.Flow!.Status);
        await app.Stores.Requests.ReplaceAsync(stored.Value with { Flow = stored.Value.Flow with { WaitingOn = WaitingOn.Agent } }, stored.ETag);
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Contains(r.Flow.Events, e => e.Step == "agent-manual" && e.Note!.Contains("dry-run simulation", StringComparison.Ordinal));
        Assert.Empty(app.Writer.Calls);
    }

    [Fact]
    public async Task A_dry_run_simulation_never_gets_a_tap_even_with_the_live_simulation_tap_switch()
    {
        var app = await App(live: false, tap: true, liveTap: true);
        await SimulatedAndApproved(app, Finance());
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task A_live_simulation_never_adds_an_account_that_already_exists_and_continues_as_a_dry_run()
    {
        var app = await App();
        app.Directory.AddUser(FinanceUser); // finding 2: e.g. a colleague's account
        var id = await SimulatedAndApproved(app, Finance());
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        var r = await app.Request(id);
        Assert.True(r is { LiveWrites: false, DryRun: true });
        Assert.Contains("already exists in Entra", r.Flow!.IdTeamTask!.Agent!.Manual);
        Assert.Null(await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id)); // a dry run again: out of the response document

        // The developer finishes it as a dry run: simulated checks, nothing assigned.
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport(), Dev)).Status);
        await app.RunWorker();
        Assert.Equal(200, (await app.ReportRoleTeam(id, TestApp.RoleReport(), Dev)).Status);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Completed, await app.Status(id));
        Assert.Empty(app.RoleWriter.Calls);
    }

    [Fact]
    public async Task A_simulation_for_the_developers_own_account_is_never_live()
    {
        var app = await App();
        var id = await Simulate(app, TestApp.Intake(enterpriseId: TestApp.SignIn(Dev), resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        var r = await app.Request(id);
        Assert.False(r.LiveWrites);
        Assert.Contains(r.Flow!.Events, e => e.Step == "simulation" && e.Note!.Contains("your own account"));
    }

    [Fact]
    public async Task A_non_developer_never_gives_both_approvals_of_a_live_simulation()
    {
        // Review 10 Nov 2026, finding S2: the four-eyes relaxations are the developer's, not every caller's on a simulation.
        var app = await App();
        var id = await Simulate(app, TestApp.Intake(enterpriseId: "sample.e2001@sample-corp.invalid", resource: "SAMPLE Finance Reports",
            accessLevel: "Viewer", owner: TestApp.SignIn(TestApp.Dual)));
        Assert.True((await app.Request(id)).LiveWrites);
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Dual)).Status); // the named Owner
        var second = await app.Approve(id, TestApp.Dual);                 // and again as Security: refused
        Assert.Equal(409, second.Status);
        Assert.Contains("same_approver", second.Body);
        Assert.Equal(RequestStatus.AwaitingSecurity, await app.Status(id));
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
    }

    [Fact]
    public async Task Only_a_developer_resubmits_a_simulation_and_a_live_one_is_checked_again()
    {
        // Review 10 Nov 2026, finding S3: a returned live simulation is changed by a developer only, and stays live only if the
        // submit-time rules still hold (here: the resubmitting developer's own account, so it becomes a dry run).
        var app = await App();
        var id = await Simulate(app, TestApp.Intake(enterpriseId: "sample.e2001@sample-corp.invalid",
            resource: "SAMPLE Finance Reports", accessLevel: "Viewer", justification: "SAMPLE: x."));
        Assert.True((await app.Request(id)).LiveWrites);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Returned, await app.Status(id));
        var etag = (await app.View(id, Dev)).Json.GetProperty("request").GetProperty("etag").GetString();

        var byRequester = await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}",
            TestApp.Intake(enterpriseId: "sample.other@sample-corp.invalid", resource: "SAMPLE Finance Reports", accessLevel: "Viewer"), etag);
        Assert.Equal(403, byRequester.Status);

        var own = await app.Call(Dev, "PUT", $"projects/{TestApp.Alpha}/requests/{id}",
            TestApp.Intake(enterpriseId: TestApp.SignIn(Dev), resource: "SAMPLE Finance Reports", accessLevel: "Viewer"), etag);
        Assert.True(own.Status == 200, own.Body);
        var r = await app.Request(id);
        Assert.True(r is { Simulated: true, LiveWrites: false });
        Assert.Contains(r.Flow!.Events, e => e.Step == "simulation" && e.Note!.Contains("your own account"));
    }

    [Theory]
    [InlineData("Prod", "Prod")]   // a Prod environment of a test tenant (Gokul, 7 Nov 2026: "Yes, Prod too")
    [InlineData("Dev", null)]      // a row for every environment
    public async Task A_live_simulation_in_a_test_tenant_is_written_for_real_in_prod_too(string environment, string? rowEnvironment)
    {
        var app = await App();
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { Environments = ["Dev", "Prod"] }, p.ETag);
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with { Environment = rowEnvironment, Version = v.Value.Version + 1 }, v.ETag);
        var id = await Simulate(app, Finance(environment: environment));
        Assert.True((await app.Request(id)).LiveWrites);
        await app.RunWorker();
        if (await app.Status(id) == RequestStatus.AwaitingOwner)
        {
            Assert.Equal(200, (await app.Approve(id, Dev)).Status);
            Assert.Equal(200, (await app.Approve(id, Dev)).Status);
            await app.RunWorker();
        }
        Assert.Contains($"create-user {FinanceUser}", app.Writer.Calls);
    }

    [Fact]
    public async Task A_tenant_another_project_calls_production_is_never_written_by_a_live_simulation()
    {
        var app = await App();
        var beta = (await app.Stores.Projects.GetAsync(TestApp.Beta, TestApp.Beta))!;
        await app.Stores.Projects.ReplaceAsync(beta.Value with { TenantId = Tenant, IsProductionTenant = null }, beta.ETag);
        var id = await Simulate(app, Finance());
        var r = await app.Request(id);
        Assert.False(r.LiveWrites); // finding 7: the tenant is shared
        Assert.Contains(r.Flow!.Events, e => e.Step == "simulation" && e.Note!.Contains("another project"));
    }

    [Fact]
    public async Task A_project_with_no_tenant_id_counts_as_the_agents_tenant_and_keeps_simulations_dry()
    {
        // Review 10 Nov 2026, finding S1: an empty tenant id is the agent's tenant for the agent's writes (Tenants.Connected),
        // so a production (or unmarked) project without one keeps every simulation in that tenant a dry run.
        var app = await App();
        var beta = (await app.Stores.Projects.GetAsync(TestApp.Beta, TestApp.Beta))!;
        await app.Stores.Projects.ReplaceAsync(beta.Value with { TenantId = null, IsProductionTenant = true }, beta.ETag);
        var id = await Simulate(app, Finance());
        Assert.False((await app.Request(id)).LiveWrites);
        var me = await app.Call(Dev, "GET", "me");
        var alpha = me.Json.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("projectId").GetString() == TestApp.Alpha);
        Assert.DoesNotContain(TestApp.Beta, alpha.GetProperty("liveSimulationRefused").GetString()); // no other project's id on /me
    }

    [Fact]
    public async Task Switched_off_after_the_approvals_the_agent_writes_nothing_and_the_developer_gets_the_id_team_task()
    {
        var on = await App();
        var id = await SimulatedAndApproved(on, Finance());
        Assert.Equal(WaitingOn.Agent, (await on.Request(id)).Flow!.WaitingOn);

        var off = on.Restart(Settings(live: false)); // switching it off is always allowed
        await off.RunWorker();
        Assert.Empty(off.Writer.Calls);
        var r = await off.Request(id);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Contains("live simulation stopped", r.Flow.IdTeamTask!.Agent!.Manual);
        Assert.Contains("IdTeamTask", (await off.View(id, Dev)).Json.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        // Finding 6: nothing was written, so it continues as a dry run the developer can finish (simulated checks).
        Assert.True(r is { LiveWrites: false, DryRun: true });
        Assert.Null(await off.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id));
        Assert.Equal(200, (await off.ReportIdTeam(id, off.IdReport(), Dev)).Status);
        await off.RunWorker();
        Assert.Equal(RequestStatus.WithRoleTeam, await off.Status(id));
    }

    [Fact]
    public void Live_simulations_are_refused_at_start_up_in_production()
    {
        var env = new Dictionary<string, string?> { ["APP_ENVIRONMENT"] = "prod", ["SIMULATION_LIVE_WRITES"] = "true" };
        var e = Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k)));
        Assert.Contains("SIMULATION_LIVE_WRITES", e.Message);
        env["APP_ENVIRONMENT"] = "test";
        Assert.True(PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k)).Workflow.SimulationLiveWrites);
    }

    [Fact]
    public void The_live_simulation_tap_and_the_tap_mail_channel_are_checked_at_start_up()
    {
        var env = new Dictionary<string, string?> { ["APP_ENVIRONMENT"] = "prod", ["SIMULATION_LIVE_TAP"] = "true" };
        Assert.Contains("SIMULATION_LIVE_TAP", Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k))).Message);
        env["APP_ENVIRONMENT"] = "test";
        Assert.True(PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k)).Workflow.SimulationLiveTap);

        // TAP_MAIL_VIA=acs: the app's ACS sender carries the TAP mail, so no TAP_MAIL_SENDER mailbox is needed.
        env["TAP_MAIL_VIA"] = "acs";
        Assert.Contains("NOTIFY_SENDER=AcsEmail", Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k))).Message);
        env["NOTIFY_SENDER"] = "AcsEmail"; env["AGENT_PROVISIONING"] = "true"; env["AGENT_TAP"] = "true";
        var s = PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.True(s.Workflow.TapMailViaAcs);
        Assert.True(s.Workflow.TapMailByAgent);
        Assert.True(WorkerStartup.Capabilities(s, _ => null).TapMail);
        env["TAP_MAIL_VIA"] = "smtp";
        Assert.Contains("TAP_MAIL_VIA", Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k))).Message);
        env["TAP_MAIL_VIA"] = null;
        Assert.False(PilotSettings.FromEnvironment(k => env.GetValueOrDefault(k)).Workflow.TapMailByAgent); // Graph without a sender mailbox
    }

    [Fact]
    public async Task The_website_says_per_project_whether_a_simulation_is_live()
    {
        var app = await App();
        var me = await app.Call(Dev, "GET", "me");
        Assert.True(me.Json.GetProperty("simulationLiveWrites").GetBoolean());
        var alpha = me.Json.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("projectId").GetString() == TestApp.Alpha);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, alpha.GetProperty("liveSimulationRefused").ValueKind);

        var marked = await App(production: null);
        var other = (await marked.Call(Dev, "GET", "me")).Json.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("projectId").GetString() == TestApp.Alpha);
        Assert.Contains("not marked", other.GetProperty("liveSimulationRefused").GetString());
    }

    private sealed class CapturingSender : INotificationSender
    {
        public List<Notification> Sent { get; } = [];
        public string Name => "Capturing";
        public bool Delivers => true;

        public Task SendAsync(Notification notification, CancellationToken ct)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }
}
