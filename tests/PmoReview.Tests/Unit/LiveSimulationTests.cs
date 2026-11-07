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
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string FinanceUser = "sample.e2001@sample.invalid";
    private const string FinanceGroup = "SAMPLE-GRP-ALPHA-FIN-VIEW";

    private static PilotSettings Settings(bool live = true, string? redirect = null) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = true, AgentOffboarding = true,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [Scope],
            AgentTenantId = Tenant, SimulationLiveWrites = live, MailRedirectTo = redirect, AppEnvironment = "test",
        },
    };

    /// <summary>SAMPLE-ALPHA named as the agent's tenant, marked not production (or as given), and the Finance row with an allow-listed role.</summary>
    private static async Task<TestApp> App(bool live = true, bool? production = false, string? redirect = null)
    {
        var app = TestApp.With(settings: Settings(live, redirect));
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = Tenant, IsProductionTenant = production }, p.ETag);
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            Role = "Reader", RoleScope = Scope, AzureRoleDefinitionId = FakeRoleWriter.Reader, AzureScope = Scope, Version = v.Value.Version + 1,
        }, v.ETag);
        return app;
    }

    private static object Finance(bool guest = false, string? environment = null) =>
        TestApp.Intake(enterpriseId: "sample.e2001@sample-corp.invalid", resource: "SAMPLE Finance Reports", accessLevel: "Viewer", guest: guest, environment: environment);

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

        // Deleting it says the access stays and keeps the response document record.
        var deleted = await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/{id}/delete", new { confirm = true });
        Assert.Equal(200, deleted.Status);
        Assert.Contains("Offboard user", deleted.Body);
        Assert.NotNull(await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id));
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
        { "Prod environment", true, false, false, "Prod" },
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
