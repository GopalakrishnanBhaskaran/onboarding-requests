using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>7 Nov 2026: "What happens next" was empty ("—") at Awaiting Prod approval. Every status names its next step.</summary>
public class NextStepTextTests
{
    [Theory]
    [MemberData(nameof(Statuses))]
    public void Every_status_names_what_happens_next(RequestStatus status)
    {
        var now = DateTimeOffset.Parse("2026-11-07T08:00:00Z");
        var flow = WorkflowState.Start(now, "sample-user-alice") with { Status = status };
        var r = new AccessRequest
        {
            Id = "sample-r1", ProjectId = "SAMPLE-ALPHA", CreatedBy = "sample-user-alice", CreatedAt = now, UpdatedBy = "sample-user-alice",
            UpdatedAt = now, Revision = 1, Flow = flow,
        };
        var next = Reports.StatusLine(r, now).Next;
        Assert.False(string.IsNullOrWhiteSpace(next) || next == "—", $"{status}: '{next}'");
    }

    public static TheoryData<RequestStatus> Statuses()
    {
        var data = new TheoryData<RequestStatus>();
        foreach (var s in Enum.GetValues<RequestStatus>()) data.Add(s);
        return data;
    }

    [Fact]
    public void Awaiting_prod_approval_says_who_decides_and_where()
    {
        var now = DateTimeOffset.Parse("2026-11-07T08:00:00Z");
        var r = new AccessRequest
        {
            Id = "sample-r2", ProjectId = "SAMPLE-ALPHA", CreatedBy = "x", CreatedAt = now, UpdatedBy = "x", UpdatedAt = now, Revision = 1,
            Flow = WorkflowState.Start(now, "x") with { Status = RequestStatus.AwaitingProdApproval },
        };
        var next = Reports.StatusLine(r, now).Next;
        Assert.Contains("ProdApprover", next);
        Assert.Contains("Escalations page", next);
    }
}
