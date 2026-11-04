using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// 4 Nov 2026, live: "the site did not start the agent". A request submitted on the website waits at AI review for the
/// worker's read-only Entra cross-check, and the worker ran group resolution and offboarding before the agent with nothing
/// between them: one failing offboarding run, group name or unreadable document (or a Cosmos/HTTP timeout, which .NET
/// raises as an OperationCanceledException) ended the whole cycle, every 5 minutes, before any request was advanced.
/// Each case below failed before the fix (the worker run threw, or the request stayed at AI review).
/// </summary>
public class WorkerIsolationTests
{
    private const string AgentTenant = "00000000-0000-4000-8000-0000000000a1"; // SAMPLE tenant id
    private const string Leaver = "sample.leaver@sample.invalid";

    private static PilotSettings Offboarding() =>
        new() { Workflow = new WorkflowSettings { AgentOffboarding = true, AgentTenantId = AgentTenant } };

    private static async Task OwnTenant(TestApp app, string project = TestApp.Alpha)
    {
        var p = (await app.Stores.Projects.GetAsync(project, project))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = AgentTenant, IsProductionTenant = false }, p.ETag);
    }

    private static async Task<IReadOnlyList<Issue>> Issues(TestApp app, string project = TestApp.Alpha) =>
        (await app.Stores.Issues.ListAsync(project)).Select(i => i.Value).ToList();

    [Fact]
    public async Task An_offboarding_run_that_hits_an_unexpected_error_does_not_stop_the_agent()
    {
        var app = TestApp.With(settings: Offboarding());
        await OwnTenant(app);
        app.Directory.AddUser(Leaver);
        await app.Stores.PeopleInventory.CreateAsync(new Person
        {
            Id = Person.MakeId(Leaver), ProjectId = TestApp.Alpha, EnterpriseId = Leaver, EnterpriseEmail = Leaver, DisplayName = "SAMPLE Leaver", Domain = "Sales",
            UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
        var plan = await app.Call(TestApp.Paul, "POST", $"offboarding/{Uri.EscapeDataString(Leaver)}?projectId={TestApp.Alpha}&mode=plan");
        Assert.True(plan.Status == 202, plan.Body);
        var id = await app.SubmitId();
        Assert.Equal(RequestStatus.AiReview, await app.Status(id)); // waits for the worker's Entra cross-check
        app.Offboarding.Crash = new InvalidOperationException("SAMPLE unexpected error in an offboarding read");

        var s = await app.RunWorker(); // threw before the fix: the request was never reached

        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Empty(s.Failed); // handled inside the job, per run
        var issue = Assert.Single(await Issues(app), i => i.Step.StartsWith("offboarding-error:", StringComparison.Ordinal));
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Contains("InvalidOperationException", issue.Summary);

        // A run that keeps failing is not retried for ever: after 3 tries the plan ends Failed (nothing was changed).
        await app.RunWorker();
        await app.RunWorker();
        var run = Assert.Single(await app.Stores.OffboardingRuns.ListAsync(TestApp.Alpha)).Value;
        Assert.Equal(OffboardingStatus.Failed, run.Status);
        Assert.Equal(3, Assert.Single(await Issues(app), i => i.Step.StartsWith("offboarding-error:", StringComparison.Ordinal)).Occurrences);
        Assert.Empty(app.Offboarding.Calls); // no write was made
    }

    [Fact]
    public async Task A_failing_group_resolution_does_not_stop_the_agent()
    {
        var mem = InMemoryDataStores.Create();
        var groups = new FaultyStore<GroupResolution>(mem.GroupResolutions);
        var app = TestApp.On(mem with { GroupResolutions = groups });
        app.ResolveGroups = true;
        var id = await app.SubmitId();
        groups.Fail = (op, _) => op == "Create" ? new InvalidOperationException("SAMPLE Cosmos write refused") : null;

        var s = await app.RunWorker();

        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Empty(s.Failed);
        Assert.Contains(await Issues(app), i => i.Step == "group-resolution-error:InvalidOperationException");
    }

    [Fact]
    public async Task A_job_that_fails_outright_is_reported_and_the_next_jobs_still_run()
    {
        var mem = InMemoryDataStores.Create();
        var notifications = new FaultyStore<Notification>(mem.Notifications);
        var app = TestApp.On(mem with { Notifications = notifications });
        var id = await app.SubmitId();
        // A notification document the dispatcher cannot read (only its partition listing fails; the agent still enqueues).
        notifications.Fail = (op, _) => op == "List" ? new InvalidOperationException("SAMPLE poison notification") : null;
        app.Clock.UtcNow = new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero); // past the daily summary hour

        var s = await app.RunWorker();

        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id)); // the agent ran before the failing job
        Assert.Contains(WorkflowWorker.MailJob, s.Failed);
        Assert.True(s.DailySummary); // a job after the failing one still ran
        foreach (var project in new[] { TestApp.Alpha, TestApp.Beta })
            Assert.Contains(await Issues(app, project), i => i.Step == $"worker-job:{WorkflowWorker.MailJob}:InvalidOperationException" && i.Severity == IssueSeverity.High);
    }

    [Fact]
    public async Task One_project_whose_requests_cannot_be_read_does_not_stop_the_other_projects()
    {
        var mem = InMemoryDataStores.Create();
        var requests = new FaultyStore<AccessRequest>(mem.Requests);
        var app = TestApp.On(mem with { Requests = requests });
        var alpha = await app.SubmitId();
        var beta = await app.SubmitId(TestApp.Intake(resource: "SAMPLE Test Portal", owner: TestApp.SignIn(TestApp.Bella)), TestApp.Bob, TestApp.Beta);
        Assert.Equal(RequestStatus.AiReview, (await app.Request(beta, TestApp.Beta)).Flow!.Status);
        // The project listed first cannot be read (e.g. a document written by a newer version); the other one must still move.
        var first = (await mem.Projects.ListAllAsync()).Select(p => p.Value.ProjectId).First(p => p is TestApp.Alpha or TestApp.Beta);
        requests.Fail = (op, key) => op == "List" && key == first ? new System.Text.Json.JsonException("SAMPLE unreadable request") : null;

        var s = await app.RunWorker();

        var (other, otherProject) = first == TestApp.Alpha ? (beta, TestApp.Beta) : (alpha, TestApp.Alpha);
        Assert.Equal(RequestStatus.AwaitingOwner, (await app.Request(other, otherProject)).Flow!.Status);
        Assert.Contains(await Issues(app, first), i => i.Step == "agent-error:reading the requests:JsonException");
        Assert.DoesNotContain(WorkflowWorker.AgentJob, s.Failed); // handled per project (ADO sync and the daily summary read them too: reported, not blocking)
    }

    [Fact]
    public async Task A_timeout_on_one_request_does_not_stop_the_requests_after_it()
    {
        var mem = InMemoryDataStores.Create();
        var requests = new FaultyStore<AccessRequest>(mem.Requests);
        var app = TestApp.On(mem with { Requests = requests });
        var stuck = await app.SubmitId();
        var next = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.e1002@sample-corp.invalid"));
        // A Cosmos or HTTP timeout is an OperationCanceledException while the worker's own token is not cancelled.
        requests.Fail = (op, key) => op == "Get" && key == stuck ? new TaskCanceledException("SAMPLE Cosmos request timeout") : null;

        await app.RunWorker(); // before the fix the exception left the agent job: no request after it was advanced

        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(next));
        requests.Fail = (_, _) => null;
        Assert.Equal(RequestStatus.AiReview, await app.Status(stuck)); // unchanged; the next run retries it
        Assert.Contains(await Issues(app), i => i.RequestId == stuck && i.Step.EndsWith(":TaskCanceledException", StringComparison.Ordinal));
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(stuck));
    }

    [Fact]
    public async Task The_worker_still_stops_when_the_host_cancels_the_run()
    {
        var mem = InMemoryDataStores.Create();
        var projects = new FaultyStore<Project>(mem.Projects);
        var app = TestApp.On(mem with { Projects = projects });
        await app.SubmitId();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        // Like Cosmos, the store honours the run's token: the host stopping (shutdown, timeout of the function) ends the run.
        projects.Fail = (_, _) => cts.IsCancellationRequested ? new OperationCanceledException(cts.Token) : null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.RunWorker(cts.Token));
        Assert.True(Failures.NotCancelled(new TaskCanceledException("timeout"), CancellationToken.None)); // a call timing out is an error to handle
        Assert.False(Failures.NotCancelled(new OperationCanceledException(cts.Token), cts.Token));
    }
}
