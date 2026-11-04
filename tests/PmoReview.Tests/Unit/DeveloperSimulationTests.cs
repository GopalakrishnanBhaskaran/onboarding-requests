using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// 4 Nov 2026 (Gokul): developers simulate a request end to end and delete requests. A simulation runs every stage with the
/// developer acting for each (Owner, Security, ID team, role team); the AI agent writes nothing to Entra or Azure for it, it
/// never enters the response document and its mails stay in the app. The Developer role grants nothing on a real request
/// except deleting it (with a reason; access already granted stays).
/// </summary>
public class DeveloperSimulationTests
{
    private const string Dev = "sample-user-dev"; // Developer in SAMPLE-ALPHA (sample data)

    /// <summary>Agent provisioning, role assignment and privileged rows all on: the strongest case for "nothing is written".</summary>
    private static async Task<TestApp> App()
    {
        var app = TestApp.With(settings: new PilotSettings
        {
            Workflow = new WorkflowSettings
            {
                AgentProvisioning = true, AgentRoleAssignment = true, AgentPrivileged = true,
                RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [TestApp.SampleAccessScope],
            },
        });
        return app; // sample-user-dev holds the Developer role in SAMPLE-ALPHA (sample data)
    }

    private static string Path(string id, string rest = "") => $"projects/{TestApp.Alpha}/requests/{id}{rest}";

    [Fact]
    public async Task A_developer_simulates_a_request_through_every_stage_and_nothing_is_written_to_Entra_or_Azure()
    {
        var app = await App();
        var sim = await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/simulate", TestApp.Intake());
        Assert.True(sim.Status == 201, sim.Body);
        var id = sim.Request.GetProperty("id").GetString()!;
        Assert.True(sim.Request.GetProperty("simulated").GetBoolean());
        await app.RunWorker(); // the agent's review with the read-only Entra cross-check, as for any request
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));

        // The developer acts for the Owner (not the named Owner) and for Security (the same person: a simulation grants nothing).
        Assert.Equal(200, (await app.Approve(id, Dev)).Status);
        Assert.Equal(200, (await app.Approve(id, Dev)).Status);
        var r = await app.Request(id);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn)); // not the agent, although provisioning is on
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls); // no user, group or membership created
        Assert.Contains("IdTeamTask", (await app.View(id, Dev)).Json.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));

        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport(), Dev)).Status);
        await app.RunWorker();
        Assert.Equal(RequestStatus.WithRoleTeam, await app.Status(id));
        Assert.Equal(200, (await app.ReportRoleTeam(id, TestApp.RoleReport(), Dev)).Status);
        await app.RunWorker();

        r = await app.Request(id);
        Assert.Equal(RequestStatus.Completed, r.Flow!.Status);
        Assert.Contains(r.Flow.FinalChecks!, c => c.Detail.StartsWith("SIMULATION:", StringComparison.Ordinal));
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.Empty(app.Mail.Sent);
        Assert.Null(app.Directory.User(TestApp.DefaultUser)); // never created
        Assert.Null(await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id)); // never in the source of truth of access
        var all = (await app.Stores.Requests.ListAsync(TestApp.Alpha)).Select(x => x.Value).ToList();
        Assert.Equal(0, Reports.Daily(TestApp.Alpha, DateOnly.FromDateTime(r.CreatedAt.UtcDateTime), all, [], app.Clock.UtcNow).Submitted); // not counted

        // Its mails stay in the app, even with a sender that delivers.
        var sender = new CapturingSender();
        await new NotificationDispatcher(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance).RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(sender.Sent, n => n.RequestId == id);
        Assert.All((await app.Stores.Notifications.ListAsync(TestApp.Alpha)).Select(n => n.Value).Where(n => n.RequestId == id && n.To.Count > 0),
            n => Assert.Equal(NotificationStatus.Recorded, n.Status));
    }

    [Fact]
    public async Task The_developer_role_gives_no_right_on_a_real_request_and_others_cannot_simulate()
    {
        var app = await App();
        Assert.Equal(403, (await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests", TestApp.Intake())).Status); // no real submit
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests/simulate", TestApp.Intake())).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "POST", $"projects/{TestApp.Alpha}/requests/simulate", TestApp.Intake())).Status);

        var real = await app.To(RequestStatus.AwaitingOwner);
        var approve = await app.Approve(real, Dev);
        Assert.Equal((403, "not_an_approver"), (approve.Status, approve.ErrorCode));
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(real));
        Assert.DoesNotContain("ApproveOwner", (await app.View(real, Dev)).Json.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));

        Assert.Equal(200, (await app.Approve(real, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(real, TestApp.Sam)).Status);
        Assert.Equal(403, (await app.Call(Dev, "POST", Path(real, "/tasks/id-team/pickup"), new { })).Status);
        Assert.Equal(403, (await app.ReportIdTeam(real, app.IdReport(), Dev)).Status);

        // The bundle path too: simulate=true needs Simulate, and a plain bundle needs SubmitRequests.
        Assert.Equal(403, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests/bundle?simulate=true", new { selectedResources = new[] { "x" } })).Status);
    }

    [Fact]
    public async Task A_developer_deletes_a_request_with_a_reason_and_everything_that_belongs_to_it()
    {
        var app = await App();
        var real = await app.To(RequestStatus.AwaitingOwner);
        Assert.Contains(await app.Stores.Runs.ListAsync(TestApp.Alpha), x => x.Value.RequestId == real);

        Assert.Equal(403, (await app.Call(TestApp.Paul, "POST", Path(real, "/delete"), new { reason = "SAMPLE test", confirm = true })).Status); // PMO: no
        Assert.Equal(404, (await app.Call(TestApp.Bob, "POST", Path(real, "/delete"), new { reason = "SAMPLE test", confirm = true })).Status);  // other project
        Assert.Equal(400, (await app.Call(Dev, "POST", Path(real, "/delete"), new { reason = "SAMPLE test" })).Status);                       // not confirmed
        Assert.Equal(400, (await app.Call(Dev, "POST", Path(real, "/delete"), new { confirm = true })).Status);                              // a real one needs a reason
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(real));

        var deleted = await app.Call(Dev, "POST", Path(real, "/delete"), new { reason = "SAMPLE: duplicate of another request", confirm = true });
        Assert.True(deleted.Status == 200, deleted.Body);
        Assert.False(deleted.Json.GetProperty("simulated").GetBoolean());
        Assert.Equal(404, (await app.View(real)).Status);
        Assert.DoesNotContain(await app.Stores.Runs.ListAsync(TestApp.Alpha), x => x.Value.RequestId == real);
        Assert.DoesNotContain(await app.Stores.Notifications.ListAsync(TestApp.Alpha), x => x.Value.RequestId == real);
        Assert.Null(await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, real)); // only pending approval: removed with it
        var audit = Assert.Single(await app.Stores.AuditEvents.ListAsync(TestApp.Alpha), a => a.Value.Action == "request-deleted").Value;
        Assert.Contains("Reason: SAMPLE: duplicate of another request", audit.Details);
        Assert.Contains($"request {real} at Awaiting Owner", audit.Details);
        await app.RunWorker(); // the worker finds nothing to do for it

        // A simulation needs no reason.
        var sim = (await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/simulate", TestApp.Intake(enterpriseId: "sample.e1009@sample-corp.invalid"))).Request.GetProperty("id").GetString()!;
        Assert.Equal(200, (await app.Call(Dev, "POST", Path(sim, "/delete"), new { confirm = true })).Status);
        Assert.Null(await app.Stores.Requests.GetAsync(TestApp.Alpha, sim));
    }

    [Fact]
    public async Task Deleting_a_completed_real_request_keeps_its_response_document_record()
    {
        var app = TestApp.Create(); // without agent provisioning: the teams report, the agent verifies
        var id = await app.To(RequestStatus.Completed);
        Assert.Equal(ResponseState.Assigned, (await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id))!.Value.State);

        var deleted = await app.Call(Dev, "POST", Path(id, "/delete"), new { reason = "SAMPLE: test cleanup", confirm = true });

        Assert.True(deleted.Status == 200, deleted.Body);
        Assert.True(deleted.Json.GetProperty("responseRecordKept").GetBoolean()); // access was granted: the record of it stays
        Assert.Contains("Offboard user", deleted.Json.GetProperty("note").GetString());
        Assert.NotNull(await app.Stores.ResponseDocument.GetAsync(TestApp.Alpha, id));
    }

    [Fact]
    public async Task A_simulation_never_makes_a_person_offboardable()
    {
        const string tenant = "00000000-0000-4000-8000-0000000000a1";
        var app = TestApp.With(settings: new PilotSettings { Workflow = new WorkflowSettings { AgentOffboarding = true, AgentTenantId = tenant } });
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = tenant, IsProductionTenant = false }, p.ETag);
        // A person known to the project only through this simulation (not in the people inventory, no real request).
        var id = (await app.Call(Dev, "POST", $"projects/{TestApp.Alpha}/requests/simulate", TestApp.Intake(enterpriseId: "sample.sim9001@sample-corp.invalid")))
            .Request.GetProperty("id").GetString()!;
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, Dev)).Status);
        Assert.Equal(200, (await app.Approve(id, Dev)).Status); // "approved" in the simulation
        var upn = (await app.Request(id)).DomainId!;
        app.Directory.AddUser(upn);
        var plan = await app.Call(TestApp.Paul, "POST", $"offboarding/{Uri.EscapeDataString(upn)}?projectId={TestApp.Alpha}&mode=plan", new { reason = "Leaver" });
        Assert.Equal(404, plan.Status); // a real person of the project only: a simulation does not count
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
