using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 (review finding 5): a retired mapping row (F4 "Retire missing rows", rollback) or one outside its I1 effective dates
/// is never reviewed as current and never granted — not through the single-request route, and not after the review either.
/// </summary>
public class RetiredRowTests
{
    private const string Row = "A-DBW-ENG-USER"; // the default SAMPLE request's row

    private static async Task Change(TestApp app, Func<MappingRow, MappingRow> change)
    {
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, Row))!;
        await app.Stores.MappingSheet.ReplaceAsync(change(v.Value) with { Version = v.Value.Version + 1 }, v.ETag);
    }

    /// <summary>The MAP_NOT_DEPRECATED result of the request's latest review run.</summary>
    private static async Task<CheckResult> NotDeprecated(TestApp app, string id)
    {
        var run = (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Select(x => x.Value).Where(x => x.RequestId == id && x.Result is not null)
            .OrderBy(x => x.QueuedAt).Last();
        return Assert.Single(run.Result!.Checks, c => c.Code == CheckCodes.MapNotDeprecated);
    }

    [Fact]
    public async Task A_request_for_a_retired_row_fails_the_review_with_the_reason()
    {
        var app = TestApp.Create();
        await Change(app, r => r with { Retired = true });
        var id = await app.SubmitId(TestApp.Intake());
        await app.RunWorker();
        Assert.Equal(RequestStatus.Returned, await app.Status(id));
        var check = await NotDeprecated(app, id);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("retired", check.Detail);
    }

    [Fact]
    public async Task A_request_for_an_expired_or_not_yet_effective_row_fails_the_review()
    {
        foreach (var (from, to) in new (DateOnly?, DateOnly?)[] { (new(2026, 1, 1), new(2026, 9, 30)), (new(2026, 12, 1), null) })
        {
            var app = TestApp.Create();
            await Change(app, r => r with { EffectiveFrom = from, EffectiveTo = to });
            var id = await app.SubmitId(TestApp.Intake());
            await app.RunWorker();
            Assert.Equal(RequestStatus.Returned, await app.Status(id));
            var check = await NotDeprecated(app, id);
            Assert.Equal(CheckStatus.Fail, check.Status);
            Assert.Contains("not in effect", check.Detail);
        }
    }

    [Fact]
    public async Task A_row_retired_after_the_review_is_reviewed_again_and_nothing_is_granted()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingSecurity);
        await Change(app, r => r with { Retired = true });
        await app.Approve(id, TestApp.Sam);
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal(RequestStatus.Returned, r.Flow!.Status);
        Assert.Null(r.Flow.IdTeamTask);
        Assert.Contains(r.Flow.Events, e => e.Step == "mapping-changed");
        Assert.Empty(app.Writer.Calls);
    }

    [Fact]
    public async Task A_row_that_ends_after_the_review_is_reviewed_again_and_nothing_is_granted()
    {
        var app = TestApp.Create();
        await Change(app, r => r with { EffectiveTo = DateOnly.FromDateTime(app.Clock.UtcNow.UtcDateTime).AddDays(1) });
        var id = await app.To(RequestStatus.AwaitingSecurity);
        app.Clock.Advance(TimeSpan.FromDays(2)); // the row ends while the request waits for Security
        await app.Approve(id, TestApp.Sam);
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.Equal(RequestStatus.Returned, r.Flow!.Status);
        Assert.Null(r.Flow.IdTeamTask);
        Assert.Empty(app.Writer.Calls);
    }

    [Fact]
    public async Task A_renamed_group_with_the_old_row_retired_is_one_match_not_a_conflict()
    {
        var app = TestApp.Create();
        await Change(app, r => r with { Retired = true });
        const string renamed = "SAMPLE-GRP-ALPHA-DBW-ENG-USERS";
        var g = app.Directory.AddGroup(renamed);
        var old = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, Row))!.Value;
        await app.Stores.MappingSheet.CreateAsync(old with { Id = "A-DBW-ENG-USER-2", RowId = "A-DBW-ENG-USER-2", EntraGroup = renamed, GroupObjectId = g.ObjectId, Retired = false, Version = 1 });
        var id = await app.SubmitId(TestApp.Intake());
        await app.RunWorker();
        var r = await app.Request(id);
        Assert.True(r.Flow!.Status == RequestStatus.AwaitingOwner, r.Flow.LastReason);
        Assert.Equal("A-DBW-ENG-USER-2", r.Flow.Scope!.MappingRowId);
    }
}
