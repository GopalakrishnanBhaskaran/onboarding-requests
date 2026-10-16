using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Domain;
using PmoReview.Core.Handoff;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Auto-assign (approved 16 Oct 2026): a second Reviewer's acceptance is the access decision. The API only queues;
/// the worker files one Entra adminAdd request; Entra adds and removes the access.
/// </summary>
public class AutoAssignTests
{
    private const string Pkg = "0b4c2a4e-6a51-4c47-9e0f-2f6f3c1d9a10";
    private const string Link = "https://myaccess.microsoft.com/@sample.onmicrosoft.com#/access-packages/" + Pkg;
    private const string Joiner = "sample.joiner@sample.onmicrosoft.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static TestApp App(bool separateReviewer = false) =>
        TestApp.With(settings: new PilotSettings { EntraHandoffEnabled = true, EntraAutoAssign = true, RequireSeparateReviewer = separateReviewer });

    private static async Task LinkMapping(TestApp app)
    {
        var m = (await app.Stores.Mappings.GetAsync(TestApp.Alpha, "A-MAP-ENG-DEV:v1"))!;
        var (l, id) = Core.Services.ReferenceDataService.AccessPackageLink(Link);
        await app.Stores.Mappings.ReplaceAsync(m.Value with { MyAccessLink = l, AccessPackageId = id }, m.ETag);
    }

    private static object Body() => new
    {
        requestType = "NewJoiner", role = "SAMPLE Data Engineer", environment = "Dev", manager = "SAMPLE Manager One",
        justification = "SAMPLE: joins the data platform team to build ingestion pipelines.",
        startDate = "2026-11-02", endDate = "2027-03-31", joinerUpn = Joiner,
    };

    private static async Task<EntraHandoff> Only(TestApp app, string requestId)
    {
        var r = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests/{requestId}/handoffs");
        Assert.True(r.Json.GetProperty("autoAssign").GetBoolean());
        return Core.Common.AppJson.Deserialize<EntraHandoff>(Assert.Single(r.Json.GetProperty("items").EnumerateArray()).GetProperty("value").GetRawText())!;
    }

    [Fact]
    public async Task Second_reviewer_acceptance_queues_the_assignment_and_self_acceptance_does_not()
    {
        // Separate-reviewer is forced on by auto-assign even though the setting says false.
        var app = App(separateReviewer: false);
        await LinkMapping(app);
        await app.Stores.UserAccess.CreateAsync(new UserAccess { Id = UserAccess.MakeId("sample-user-dual", TestApp.Alpha), UserId = "sample-user-dual", ProjectId = TestApp.Alpha, Roles = [AppRole.PMO, AppRole.Reviewer], IsSample = true });
        var id = await app.CreateRequest(user: "sample-user-dual", body: Body());
        var runId = (await app.Analyze(id, user: "sample-user-dual")).Json.GetProperty("runId").GetString()!;

        await app.Call("sample-user-dual", "POST", $"projects/{TestApp.Alpha}/runs/{runId}/decisions", new { outcome = "Accepted" });
        var first = await Only(app, id);
        Assert.Equal(HandoffStatus.NotEligible, first.Status);
        Assert.Contains("different Reviewer", first.Reason);

        await app.Call(TestApp.Rita, "POST", $"projects/{TestApp.Alpha}/runs/{runId}/decisions", new { outcome = "Accepted" });
        var h = await Only(app, id);
        Assert.Equal(HandoffStatus.Queued, h.Status);
        Assert.Equal((Pkg, Joiner, TestApp.Rita), (h.AccessPackageId, h.JoinerUpn, h.AcceptedBy));
        Assert.Null(h.EntraRequestId);
    }

    [Fact]
    public void Order_uses_the_reviewed_period_and_never_starts_in_the_past()
    {
        var h = Handoff() with { RequestedStart = new DateOnly(2026, 11, 2), RequestedEnd = new DateOnly(2027, 3, 31) };
        var o = AssignmentOrder.For(h, Now);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), o.Start);
        Assert.Equal(new DateTimeOffset(2027, 3, 31, 23, 59, 59, TimeSpan.Zero), o.End);
        Assert.Equal((Pkg, Joiner), (o.AccessPackageId, o.JoinerUpn));
        Assert.DoesNotContain("@", o.Justification); // ids only, no personal data in Entra's justification

        Assert.Equal(Now, AssignmentOrder.For(h with { RequestedStart = new DateOnly(2026, 10, 1) }, Now).Start);
        Assert.Equal("EndDatePassed", Assert.Throws<EntraWriteException>(() => AssignmentOrder.For(h with { RequestedEnd = new DateOnly(2026, 10, 1) }, Now)).Category);
        Assert.Equal("IncompleteHandoff", Assert.Throws<EntraWriteException>(() => AssignmentOrder.For(h with { RequestedEnd = null }, Now)).Category);
    }

    [Fact]
    public async Task Worker_files_one_request_then_follows_it_to_delivered()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        var reader = new FakeReader();
        var writer = new FakeWriter { Result = new EntraRequest("req-app-1", "adminAdd", "submitted", null, Now, AppSubject, Now, null) };
        var clock = new FixedClock(Now);
        HandoffSync Sync() => new(stores, reader, clock, NullLogger<HandoffSync>.Instance, writer);

        var s1 = await Sync().RunOnceAsync(CancellationToken.None);
        var h = Get(stores);
        Assert.Equal((1, 1, 0), (s1.Checked, s1.Changed, s1.Errors));
        Assert.Equal(HandoffStatus.PendingApproval, h.Status);
        Assert.Equal(("req-app-1", "adminAdd"), (h.EntraRequestId, h.EntraRequestType));
        Assert.Single(writer.Orders);

        // Next run: the app's request (requestor = the app, not the joiner) is matched by id; no second write.
        reader.Requests = [new EntraRequest("req-app-1", "adminAdd", "delivered", null, Now, AppSubject, Now, null)];
        reader.Assignments = [new EntraAssignment("asg-1", "delivered", null, new EntraSubject("oid-1", Joiner, Joiner, "SAMPLE Joiner"), Now, new DateTimeOffset(2027, 3, 31, 23, 59, 59, TimeSpan.Zero), "afterDateTime", null)];
        clock.Advance(TimeSpan.FromMinutes(5));
        await Sync().RunOnceAsync(CancellationToken.None);
        h = Get(stores);
        Assert.Equal(HandoffStatus.Delivered, h.Status);
        Assert.False(h.EndDateExceedsRequest);
        Assert.False(h.PreExistingAssignment);
        Assert.Single(writer.Orders);
    }

    [Fact]
    public async Task App_request_not_yet_listed_keeps_its_status()
    {
        var h = Handoff() with { Status = HandoffStatus.PendingApproval, EntraRequestId = "req-app-1", EntraRequestType = "adminAdd" };
        Assert.Equal(HandoffStatus.PendingApproval, HandoffSync.Apply(h, [], [], Now).Status);
        Assert.Equal(HandoffStatus.Queued, HandoffSync.Apply(Handoff(), [], [], Now).Status);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("JoinerNotFound")]
    [InlineData("CatalogNotAllowed")]
    [InlineData("GraphForbidden")]
    public async Task Write_failure_keeps_the_record_queued_with_the_category(string category)
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        var writer = new FakeWriter { Fail = category };
        var s = await new HandoffSync(stores, new FakeReader(), new FixedClock(Now), NullLogger<HandoffSync>.Instance, writer).RunOnceAsync(CancellationToken.None);
        var h = Get(stores);
        Assert.Equal(1, s.Errors);
        Assert.Equal(HandoffStatus.Queued, h.Status);
        Assert.Equal(category, h.LastSyncError);
        Assert.Null(h.EntraRequestId);
    }

    [Fact]
    public async Task Without_a_writer_nothing_is_written_and_the_record_says_why()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        await new HandoffSync(stores, new FakeReader(), new FixedClock(Now), NullLogger<HandoffSync>.Instance).RunOnceAsync(CancellationToken.None);
        Assert.Equal(("AutoAssignOff", HandoffStatus.Queued), (Get(stores).LastSyncError, Get(stores).Status));
    }

    [Fact]
    public async Task Joiner_who_already_holds_the_package_is_not_assigned_again()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        var reader = new FakeReader { Assignments = [new EntraAssignment("asg-0", "delivered", null, new EntraSubject("oid-1", Joiner, Joiner, null), Now.AddDays(-30), Now.AddDays(60), "afterDateTime", null)] };
        var writer = new FakeWriter();
        await new HandoffSync(stores, reader, new FixedClock(Now), NullLogger<HandoffSync>.Instance, writer).RunOnceAsync(CancellationToken.None);
        var h = Get(stores);
        Assert.Equal(HandoffStatus.Delivered, h.Status);
        Assert.True(h.PreExistingAssignment);
        Assert.Empty(writer.Orders);
    }

    private static readonly EntraSubject AppSubject = new("sp-oid", null, null, "func-pmo-worker");

    private static EntraHandoff Handoff() => new()
    {
        Id = "req1.r1", ProjectId = TestApp.Alpha, RequestId = "req1", RequestRevision = 1, ReviewRunId = "req1.r1.a0",
        AcceptedBy = TestApp.Rita, Status = HandoffStatus.Queued, JoinerUpn = Joiner, AccessPackageId = Pkg, MyAccessLink = Link,
        MappingId = "A-MAP-ENG-DEV", MappingVersion = 2,
        RequestedStart = new DateOnly(2026, 11, 2), RequestedEnd = new DateOnly(2027, 3, 31), CreatedAt = Now.AddMinutes(-1),
    };

    private static EntraHandoff Get(Core.Storage.DataStores stores) => stores.Handoffs.GetAsync(TestApp.Alpha, "req1.r1").Result!.Value;

    private sealed class FakeReader : IEntraGovernanceReader
    {
        public List<EntraRequest> Requests { get; set; } = [];
        public List<EntraAssignment> Assignments { get; set; } = [];
        public Task<IReadOnlyList<EntraRequest>> ListRequestsAsync(string accessPackageId, CancellationToken ct) => Task.FromResult<IReadOnlyList<EntraRequest>>(Requests);
        public Task<IReadOnlyList<EntraAssignment>> ListAssignmentsAsync(string accessPackageId, CancellationToken ct) => Task.FromResult<IReadOnlyList<EntraAssignment>>(Assignments);
    }

    private sealed class FakeWriter : IEntraAssignmentWriter
    {
        public List<AssignmentOrder> Orders { get; } = [];
        public EntraRequest? Result { get; init; }
        public string? Fail { get; init; }
        public Task<EntraRequest> RequestAssignmentAsync(AssignmentOrder order, CancellationToken ct)
        {
            if (Fail is not null) throw new EntraWriteException(Fail);
            Orders.Add(order);
            return Task.FromResult(Result ?? new EntraRequest("req-x", "adminAdd", "submitted", null, Now, null, order.Start, order.End));
        }
    }
}
