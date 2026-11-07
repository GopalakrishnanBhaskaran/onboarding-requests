using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// 7 Nov 2026 (live): a second request for access another open request already asks for was returned with "This user
/// already has this access … (PendingApproval …)". The access is not granted yet: the reason now says it is already requested.
/// </summary>
public class DuplicateRequestTests
{
    [Fact]
    public async Task A_second_request_for_access_still_awaiting_a_decision_says_it_is_already_requested()
    {
        var app = TestApp.With();
        var first = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.dup1@sample-corp.invalid"));
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(first));

        var second = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.dup1@sample-corp.invalid"));
        await app.RunWorker();
        var r = await app.Request(second);
        Assert.Equal(RequestStatus.Returned, r.Flow!.Status);
        Assert.Contains("already requested for this user in another open request", r.Flow.LastReason);
        Assert.Contains(first, r.Flow.LastReason);
        Assert.DoesNotContain("already has this access", r.Flow.LastReason);
    }
}
