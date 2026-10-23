using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Security;

/// <summary>Stage 3: only the named Owner, then Security, via the app, tied to the request id and the review they saw.</summary>
public class ApprovalTests
{
    [Fact]
    public async Task Owner_who_is_not_named_on_the_request_is_ignored_logged_and_security_alerted()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        var r = await app.Approve(id, TestApp.Otto);
        Assert.Equal(403, r.Status);
        Assert.Equal("not_an_approver", r.ErrorCode);
        Assert.Contains("Security has been alerted", r.Json.GetProperty("error").GetProperty("message").GetString());

        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Empty((await app.Request(id)).Flow!.Approvals);
        var issue = Assert.Single(await app.Issues(id), i => i.Kind == IssueKind.UnauthorizedApproval);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        var alert = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.SecurityAlert);
        Assert.Equal(["sample.security.alpha@sample.invalid"], alert.To);
        Assert.Contains(TestApp.SignIn(TestApp.Otto), alert.Body);
    }

    [Theory]
    [InlineData(TestApp.Alice)] // requester
    [InlineData(TestApp.Paul)]  // PMO: informed only
    [InlineData(TestApp.Ivan)]  // ID team
    [InlineData(TestApp.Sam)]   // Security cannot approve the Owner stage
    public async Task Nobody_else_approves_the_owner_stage(string user)
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        Assert.Equal(403, (await app.Approve(id, user)).Status);
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.UnauthorizedApproval);
    }

    [Theory]
    [InlineData(TestApp.Olivia)] // the Owner cannot also give the Security approval
    [InlineData(TestApp.Rose)]
    public async Task Only_security_approves_the_security_stage(string user)
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingSecurity);
        Assert.Equal(403, (await app.Approve(id, user)).Status);
        Assert.Equal(RequestStatus.AwaitingSecurity, await app.Status(id));
    }

    [Fact]
    public async Task Caller_without_project_access_gets_not_found_and_nothing_changes()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        Assert.Equal(404, (await app.Approve(id, TestApp.Stranger)).Status);
        Assert.Equal(404, (await app.Approve(id, TestApp.Bella)).Status); // other project
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    [Fact]
    public async Task Same_person_cannot_give_both_owner_and_security_approval()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(owner: TestApp.SignIn(TestApp.Dual)));
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Dual)).Status);
        var r = await app.Approve(id, TestApp.Dual);
        Assert.Equal(409, r.Status);
        Assert.Equal("same_approver", r.ErrorCode);
        Assert.Equal(RequestStatus.AwaitingSecurity, await app.Status(id));
        var view = await app.View(id, TestApp.Dual);
        Assert.Empty(view.Json.GetProperty("actions").EnumerateArray());
    }

    [Fact]
    public async Task Approval_must_reference_the_current_review()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        var r = await app.Approve(id, TestApp.Olivia, runId: "someone-else.r1.a0");
        Assert.Equal(409, r.Status);
        Assert.Equal("stale_review", r.ErrorCode);
    }

    [Fact]
    public async Task Approval_when_nothing_is_pending_is_a_conflict()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(resource: "SAMPLE Legacy Tool"));
        var r = await app.Approve(id, TestApp.Olivia, runId: "x");
        Assert.Equal(409, r.Status);
        Assert.Equal("not_awaiting_approval", r.ErrorCode);
    }

    [Fact]
    public async Task Mapping_sheet_change_before_approval_reruns_the_review_and_does_not_record_the_approval()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        var oldRun = (await app.Request(id)).Flow!.Scope!.ReviewRunId;
        var row = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-DBW-ENG-USER"))!;
        await app.Stores.MappingSheet.ReplaceAsync(row.Value with { SourceReference = "SAMPLE-PMO-SHEET row 2 (edited)", RoleScope = "SAMPLE dbw-alpha-test", Version = 2 }, row.ETag);

        var r = await app.Approve(id, TestApp.Olivia, runId: oldRun);
        Assert.Equal(409, r.Status);
        Assert.Equal("mapping_changed", r.ErrorCode);
        var req = await app.Request(id);
        Assert.Empty(req.Flow!.Approvals);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.MappingChangedAfterReview);

        await app.RunWorker();
        req = await app.Request(id);
        Assert.Equal(RequestStatus.AwaitingOwner, req.Flow!.Status);
        Assert.NotEqual(oldRun, req.Flow.Scope!.ReviewRunId);
        Assert.Equal("SAMPLE dbw-alpha-test", req.Flow.Scope.RoleScope);
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
    }

    [Fact]
    public async Task Approvals_carry_the_review_summary_and_are_recorded_with_who_when_and_channel()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.WithIdTeam);
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal(2, flow.Approvals.Count);
        Assert.All(flow.Approvals, a =>
        {
            Assert.Equal("App", a.Channel);
            Assert.Equal(flow.Scope!.ReviewRunId, a.ReviewRunId);
            Assert.Equal(ApprovalDecision.Approved, a.Decision);
        });
        Assert.Equal(TestApp.SignIn(TestApp.Olivia), flow.CurrentApproval(ApprovalStage.Owner)!.ByName);
        Assert.Equal(TestApp.SignIn(TestApp.Sam), flow.CurrentApproval(ApprovalStage.Security)!.ByName);
        var securityAsk = (await app.Notifications(id)).Last(n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Contains("AI review summary", securityAsk.Body);
        Assert.Contains($"Owner approval: {TestApp.SignIn(TestApp.Olivia)}", securityAsk.Body);
    }

    [Fact]
    public async Task Approver_actions_are_offered_only_to_the_right_person()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        Assert.Equal(["ApproveOwner"], (await app.View(id, TestApp.Olivia)).Json.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Empty((await app.View(id, TestApp.Otto)).Json.GetProperty("actions").EnumerateArray());
        var queue = await app.Call(TestApp.Olivia, "GET", "queue");
        Assert.Contains(queue.Json.GetProperty("items").EnumerateArray(), i => i.GetProperty("action").GetString() == "ApproveOwner");
        Assert.Empty((await app.Call(TestApp.Otto, "GET", "queue")).Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Remarks_are_masked_before_they_are_stored_or_sent()
    {
        var app = TestApp.Create();
        var id = await app.To(RequestStatus.AwaitingOwner);
        await app.Approve(id, TestApp.Olivia, "Rejected", "SAMPLE: not needed; tap: Xy7-Secret-Pass");
        var a = (await app.Request(id)).Flow!.Approvals.Single();
        Assert.DoesNotContain("Xy7-Secret-Pass", a.Remarks);
        Assert.All(await app.Notifications(id), n => Assert.DoesNotContain("Xy7-Secret-Pass", n.Body));
    }
}
