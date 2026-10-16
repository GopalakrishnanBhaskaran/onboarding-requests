using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Handoff;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Background sync: mirrors Entra's request and assignment state onto the hand-off record.</summary>
public class HandoffSyncTests
{
    private const string Pkg = "0b4c2a4e-6a51-4c47-9e0f-2f6f3c1d9a10";
    private const string Joiner = "sample.joiner@sample.onmicrosoft.com";
    private static readonly DateTimeOffset Created = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Created.AddHours(1);

    private static EntraHandoff Handoff(HandoffStatus status = HandoffStatus.AwaitingJoinerRequest) => new()
    {
        Id = "req1.r1", ProjectId = TestApp.Alpha, RequestId = "req1", RequestRevision = 1, ReviewRunId = "req1.r1.a0",
        AcceptedBy = TestApp.Rita, Status = status, JoinerUpn = Joiner, AccessPackageId = Pkg,
        MyAccessLink = "https://myaccess.microsoft.com/#/access-packages/" + Pkg,
        RequestedStart = new DateOnly(2026, 11, 2), RequestedEnd = new DateOnly(2027, 3, 31), CreatedAt = Created,
    };

    private static EntraSubject Subject(string upn = Joiner) => new("oid-1", upn, upn, "SAMPLE Joiner");

    private static EntraRequest Req(string state, DateTimeOffset? at = null, string upn = Joiner, string type = "userAdd", DateTimeOffset? end = null) =>
        new("req-" + state, type, state, null, at ?? Created.AddMinutes(5), Subject(upn), Created, end ?? new DateTimeOffset(2027, 3, 31, 0, 0, 0, TimeSpan.Zero));

    private static EntraAssignment Asg(string state, DateTimeOffset? end = null, string expiration = "afterDateTime", DateTimeOffset? start = null, string upn = Joiner) =>
        new("asg-1", state, null, Subject(upn), start ?? Created.AddMinutes(30), end ?? new DateTimeOffset(2027, 3, 31, 0, 0, 0, TimeSpan.Zero), expiration, null);

    [Theory]
    [InlineData("pendingApproval", HandoffStatus.PendingApproval)]
    [InlineData("submitted", HandoffStatus.PendingApproval)]
    [InlineData("scheduled", HandoffStatus.Delivering)]
    [InlineData("denied", HandoffStatus.Denied)]
    [InlineData("canceled", HandoffStatus.Cancelled)]
    [InlineData("deliveryFailed", HandoffStatus.Failed)]
    public void Request_state_maps_to_status(string state, HandoffStatus expected)
    {
        var h = HandoffSync.Apply(Handoff(), [Req(state)], [], Now);
        Assert.Equal(expected, h.Status);
        Assert.Equal(state, h.EntraRequestState);
        Assert.Single(h.History);
        Assert.Equal(Now, h.LastCheckedAt);
    }

    [Fact]
    public void Delivered_assignment_wins_and_records_the_period()
    {
        var h = HandoffSync.Apply(Handoff(HandoffStatus.PendingApproval), [Req("delivered")], [Asg("delivered")], Now);
        Assert.Equal(HandoffStatus.Delivered, h.Status);
        Assert.Equal("delivered", h.EntraAssignmentState);
        Assert.Equal(new DateTimeOffset(2027, 3, 31, 0, 0, 0, TimeSpan.Zero), h.AccessEnd);
        Assert.False(h.EndDateExceedsRequest);
        Assert.False(h.PreExistingAssignment);
    }

    [Fact]
    public void End_date_later_than_reviewed_or_missing_is_flagged()
    {
        Assert.True(HandoffSync.Apply(Handoff(), [Req("delivered")], [Asg("delivered", end: new DateTimeOffset(2027, 6, 30, 0, 0, 0, TimeSpan.Zero))], Now).EndDateExceedsRequest);
        Assert.True(HandoffSync.Apply(Handoff(), [Req("delivered")], [Asg("delivered", expiration: "noExpiration", end: DateTimeOffset.MaxValue)], Now).EndDateExceedsRequest);
        Assert.False(HandoffSync.Apply(Handoff(), [Req("delivered")], [Asg("delivered", end: new DateTimeOffset(2027, 3, 31, 23, 0, 0, TimeSpan.Zero))], Now).EndDateExceedsRequest);
    }

    [Fact]
    public void Other_users_and_older_requests_are_ignored()
    {
        var h = HandoffSync.Apply(Handoff(),
            [Req("pendingApproval", upn: "someone.else@sample.onmicrosoft.com"), Req("denied", at: Created.AddDays(-3))],
            [Asg("delivered", upn: "someone.else@sample.onmicrosoft.com")], Now);
        Assert.Equal(HandoffStatus.AwaitingJoinerRequest, h.Status);
        Assert.Empty(h.History);
    }

    [Fact]
    public void Latest_request_wins_after_a_denial()
    {
        var h = HandoffSync.Apply(Handoff(HandoffStatus.Denied), [Req("denied"), Req("pendingApproval", at: Created.AddHours(2))], [], Now);
        Assert.Equal(HandoffStatus.PendingApproval, h.Status);
    }

    [Fact]
    public void Expiry_is_reported_only_after_delivery()
    {
        Assert.Equal(HandoffStatus.Expired, HandoffSync.Apply(Handoff(HandoffStatus.Delivered), [Req("delivered")], [Asg("expired")], Now).Status);
        // An old expired assignment from before this hand-off does not count.
        Assert.Equal(HandoffStatus.AwaitingJoinerRequest, HandoffSync.Apply(Handoff(), [], [Asg("expired", start: Created.AddDays(-200))], Now).Status);
    }

    [Fact]
    public void Pre_existing_assignment_is_noted()
    {
        var h = HandoffSync.Apply(Handoff(), [], [Asg("delivered", start: Created.AddDays(-30))], Now);
        Assert.Equal(HandoffStatus.Delivered, h.Status);
        Assert.True(h.PreExistingAssignment);
    }

    [Fact]
    public void Matching_is_case_insensitive_on_principal_name()
    {
        Assert.Equal(HandoffStatus.PendingApproval, HandoffSync.Apply(Handoff(), [Req("pendingApproval", upn: "Sample.Joiner@SAMPLE.onmicrosoft.com")], [], Now).Status);
    }

    [Fact]
    public async Task Run_once_updates_open_records_skips_closed_ones_and_survives_entra_errors()
    {
        var stores = InMemoryDataStores.Create();
        await stores.Handoffs.CreateAsync(Handoff());
        await stores.Handoffs.CreateAsync(Handoff() with { Id = "req2.r1", RequestId = "req2", Status = HandoffStatus.NotEligible, Reason = "SAMPLE" });
        var reader = new FakeReader { Requests = [Req("pendingApproval")] };
        var clock = new FixedClock(Now);

        var summary = await new HandoffSync(stores, reader, clock, NullLogger<HandoffSync>.Instance).RunOnceAsync(CancellationToken.None);
        Assert.Equal((1, 1, 0), (summary.Checked, summary.Changed, summary.Errors));
        Assert.Equal(HandoffStatus.PendingApproval, (await stores.Handoffs.GetAsync(TestApp.Alpha, "req1.r1"))!.Value.Status);
        Assert.Equal(HandoffStatus.NotEligible, (await stores.Handoffs.GetAsync(TestApp.Alpha, "req2.r1"))!.Value.Status);
        Assert.Equal([Pkg], reader.Packages.Distinct());

        reader.Fail = true;
        clock.Advance(TimeSpan.FromMinutes(5));
        summary = await new HandoffSync(stores, reader, clock, NullLogger<HandoffSync>.Instance).RunOnceAsync(CancellationToken.None);
        var after = (await stores.Handoffs.GetAsync(TestApp.Alpha, "req1.r1"))!.Value;
        Assert.Equal(1, summary.Errors);
        Assert.Equal(HandoffStatus.PendingApproval, after.Status); // last known status kept
        Assert.Equal("GraphForbidden", after.LastSyncError);
    }

    private sealed class FakeReader : IEntraGovernanceReader
    {
        public List<EntraRequest> Requests { get; init; } = [];
        public List<EntraAssignment> Assignments { get; init; } = [];
        public List<string> Packages { get; } = [];
        public bool Fail { get; set; }

        public Task<IReadOnlyList<EntraRequest>> ListRequestsAsync(string accessPackageId, CancellationToken ct)
        {
            Packages.Add(accessPackageId);
            if (Fail) throw new EntraReadException("GraphForbidden");
            return Task.FromResult<IReadOnlyList<EntraRequest>>(Requests);
        }

        public Task<IReadOnlyList<EntraAssignment>> ListAssignmentsAsync(string accessPackageId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EntraAssignment>>(Assignments);
    }
}
