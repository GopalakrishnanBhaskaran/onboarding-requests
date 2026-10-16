using PmoReview.Adapters;
using PmoReview.Core.Domain;
using PmoReview.Core.Handoff;
using PmoReview.Core.Storage;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>Phase 2: an accepted, ready review becomes an Entra hand-off record (a link, never an approval).</summary>
public class HandoffTests
{
    private const string PackageId = "0b4c2a4e-6a51-4c47-9e0f-2f6f3c1d9a10";
    private const string Link = "https://myaccess.microsoft.com/@sample.onmicrosoft.com#/access-packages/" + PackageId;
    private const string Joiner = "sample.joiner@sample.onmicrosoft.com";

    private static TestApp App(bool enabled = true, bool separateReviewer = false) =>
        TestApp.With(settings: new PilotSettings { EntraHandoffEnabled = enabled, RequireSeparateReviewer = separateReviewer });

    /// <summary>Gives the seeded SAMPLE Data Engineer / Dev mapping (v1) an access package, as a Maintainer would.</summary>
    private static async Task LinkMapping(TestApp app, string link = Link)
    {
        var m = (await app.Stores.Mappings.GetAsync(TestApp.Alpha, "A-MAP-ENG-DEV:v1"))!;
        var (l, id) = Core.Services.ReferenceDataService.AccessPackageLink(link);
        await app.Stores.Mappings.ReplaceAsync(m.Value with { MyAccessLink = l, AccessPackageId = id }, m.ETag);
    }

    private static object Body(string? joiner = Joiner, string role = "SAMPLE Data Engineer") => new
    {
        requestType = "NewJoiner", role, environment = "Dev", manager = "SAMPLE Manager One",
        justification = "SAMPLE: joins the data platform team to build ingestion pipelines.",
        startDate = "2026-11-02", endDate = "2027-03-31", joinerUpn = joiner,
    };

    private static async Task<(string RequestId, string RunId)> ReviewedRequest(TestApp app, object? body = null)
    {
        var id = await app.CreateRequest(body: body ?? Body());
        var runId = (await app.Analyze(id)).Json.GetProperty("runId").GetString()!;
        return (id, runId);
    }

    private static Task<Resp> Decide(TestApp app, string runId, string outcome = "Accepted", string user = TestApp.Rita, string? comment = null) =>
        app.Call(user, "POST", $"projects/{TestApp.Alpha}/runs/{runId}/decisions", new { outcome, comment });

    private static async Task<List<EntraHandoff>> Handoffs(TestApp app, string requestId, string user = TestApp.Alice)
    {
        var r = await app.Call(user, "GET", $"projects/{TestApp.Alpha}/requests/{requestId}/handoffs");
        Assert.Equal(200, r.Status);
        return r.Json.GetProperty("items").EnumerateArray()
            .Select(i => Core.Common.AppJson.Deserialize<EntraHandoff>(i.GetProperty("value").GetRawText())!).ToList();
    }

    [Fact]
    public async Task Accepted_ready_review_issues_the_my_access_link_from_the_pinned_mapping()
    {
        var app = App();
        await LinkMapping(app);
        var (id, runId) = await ReviewedRequest(app);
        Assert.Equal("ReadyForHumanReview", (await app.Run(runId)).Value.GetProperty("readiness").GetString());

        Assert.Equal(201, (await Decide(app, runId)).Status);

        var h = Assert.Single(await Handoffs(app, id));
        Assert.Equal(HandoffStatus.AwaitingJoinerRequest, h.Status);
        Assert.Equal(Link, h.MyAccessLink);
        Assert.Equal(PackageId, h.AccessPackageId);
        Assert.Equal(Joiner, h.JoinerUpn);
        Assert.Equal(("A-MAP-ENG-DEV", 1), (h.MappingId, h.MappingVersion));
        Assert.Equal(new DateOnly(2027, 3, 31), h.RequestedEnd);
        Assert.Equal(TestApp.Rita, h.AcceptedBy);
        Assert.Equal($"{id}.r1", h.Id);
    }

    [Fact]
    public async Task Disabled_by_default_creates_nothing()
    {
        var app = App(enabled: false);
        await LinkMapping(app);
        var (id, runId) = await ReviewedRequest(app);
        await Decide(app, runId);

        var r = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests/{id}/handoffs");
        Assert.False(r.Json.GetProperty("enabled").GetBoolean());
        Assert.Empty(await app.Stores.Handoffs.ListAllAsync());
    }

    [Theory]
    [InlineData("Corrected")]
    [InlineData("Escalated")]
    public async Task Only_accepted_creates_a_handoff(string outcome)
    {
        var app = App();
        await LinkMapping(app);
        var (_, runId) = await ReviewedRequest(app);
        Assert.Equal(201, (await Decide(app, runId, outcome, comment: "SAMPLE comment")).Status);
        Assert.Empty(await app.Stores.Handoffs.ListAllAsync());
    }

    [Fact]
    public async Task Accepting_a_review_that_needs_information_creates_nothing()
    {
        var app = App();
        await LinkMapping(app);
        var (_, runId) = await ReviewedRequest(app, Body(role: "data engineer")); // no mapping -> NeedsInformation
        Assert.Equal("NeedsInformation", (await app.Run(runId)).Value.GetProperty("readiness").GetString());
        await Decide(app, runId);
        Assert.Empty(await app.Stores.Handoffs.ListAllAsync());
    }

    [Fact]
    public async Task Mapping_without_access_package_is_not_eligible()
    {
        var app = App();
        var (id, runId) = await ReviewedRequest(app);
        await Decide(app, runId);

        var h = Assert.Single(await Handoffs(app, id));
        Assert.Equal(HandoffStatus.NotEligible, h.Status);
        Assert.Contains("no Entra access package", h.Reason);
        Assert.Null(h.MyAccessLink);
    }

    [Fact]
    public async Task Missing_joiner_is_not_eligible()
    {
        var app = App();
        await LinkMapping(app);
        var (id, runId) = await ReviewedRequest(app, Body(joiner: null));
        await Decide(app, runId);

        var h = Assert.Single(await Handoffs(app, id));
        Assert.Equal(HandoffStatus.NotEligible, h.Status);
        Assert.Contains("no new joiner sign-in name", h.Reason);
    }

    [Fact]
    public async Task Edited_request_after_review_is_not_eligible()
    {
        var app = App();
        await LinkMapping(app);
        var (id, runId) = await ReviewedRequest(app);
        var current = await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/requests/{id}");
        Assert.Equal(200, (await app.Call(TestApp.Alice, "PUT", $"projects/{TestApp.Alpha}/requests/{id}", Body(joiner: "someone.else@sample.onmicrosoft.com"), current.ETag)).Status);

        await Decide(app, runId);
        var h = Assert.Single(await Handoffs(app, id));
        Assert.Equal(HandoffStatus.NotEligible, h.Status);
        Assert.Contains("edited after this review", h.Reason);
        Assert.Equal(Joiner, h.JoinerUpn); // from the reviewed snapshot, never the live request
    }

    [Fact]
    public async Task Separate_reviewer_rule_blocks_self_acceptance_until_another_reviewer_accepts()
    {
        var app = App(separateReviewer: true);
        await LinkMapping(app);
        // Bella-like dual role in Alpha: give Rita PMO too, so she creates, analyses and accepts herself.
        await app.Stores.UserAccess.CreateAsync(new UserAccess { Id = UserAccess.MakeId("sample-user-dual", TestApp.Alpha), UserId = "sample-user-dual", ProjectId = TestApp.Alpha, Roles = [AppRole.PMO, AppRole.Reviewer], IsSample = true });
        var id = await app.CreateRequest(user: "sample-user-dual", body: Body());
        var runId = (await app.Analyze(id, user: "sample-user-dual")).Json.GetProperty("runId").GetString()!;

        await Decide(app, runId, user: "sample-user-dual");
        Assert.Equal(HandoffStatus.NotEligible, Assert.Single(await Handoffs(app, id)).Status);

        await Decide(app, runId, user: TestApp.Rita);
        var h = Assert.Single(await Handoffs(app, id));
        Assert.Equal(HandoffStatus.AwaitingJoinerRequest, h.Status);
        Assert.Equal(TestApp.Rita, h.AcceptedBy);
    }

    [Fact]
    public async Task Handoff_uses_the_reviewed_mapping_version_not_a_later_one()
    {
        var app = App();
        await LinkMapping(app);
        var (id, runId) = await ReviewedRequest(app);

        // A Maintainer publishes v2 with a different package after the review.
        const string other = "https://myaccess.microsoft.com/#/access-packages/9f9f9f9f-0000-4000-8000-000000000001";
        var draft = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/mappings", new
        {
            mappingId = "A-MAP-ENG-DEV", businessRole = "SAMPLE Data Engineer", environment = "Dev", groupReference = "SAMPLE-GRP-ALPHA-DEV-ENG",
            myAccessLink = other, effectiveFrom = "2026-01-01", sourceReference = "SAMPLE-RBAC-ALPHA row 4",
        });
        Assert.Equal(201, draft.Status);
        Assert.Equal(200, (await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/mappings/A-MAP-ENG-DEV:v2/status", new { status = "Published" }, draft.ETag)).Status);

        await Decide(app, runId);
        Assert.Equal(PackageId, Assert.Single(await Handoffs(app, id)).AccessPackageId);
    }

    [Fact]
    public async Task Other_projects_cannot_read_handoffs_and_no_route_writes_them()
    {
        var app = App();
        await LinkMapping(app);
        var (id, runId) = await ReviewedRequest(app);
        await Decide(app, runId);

        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Alpha}/requests/{id}/handoffs")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Mark, "GET", $"projects/{TestApp.Alpha}/requests/{id}/handoffs")).Status); // Maintainer: no ViewRuns
        foreach (var method in new[] { "POST", "PUT" })
            Assert.Equal(405, (await app.Call(TestApp.Rita, method, $"projects/{TestApp.Alpha}/requests/{id}/handoffs", new { status = "Delivered" })).Status);
        // The one non-GET hand-off route reveals a Temporary Access Pass (writes the pass document, never the hand-off).
        Assert.Equal(["POST projects/{projectId}/handoffs/{handoffId}/pass/reveal"],
            app.Host.Routes.Where(r => r.Contains("handoffs") && !r.StartsWith("GET ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Invalid_joiner_and_access_package_links_are_rejected()
    {
        var app = App();
        Assert.Equal(400, (await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", Body(joiner: "not an upn"))).Status);
        var ok = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", Body(joiner: "Sample.Joiner@Sample.OnMicrosoft.com"));
        Assert.Equal(Joiner, ok.Value.GetProperty("joinerUpn").GetString());

        foreach (var bad in new[] { "https://evil.example/#/access-packages/" + PackageId, "https://myaccess.microsoft.com/#/access-packages/not-a-guid", "javascript:alert(1)" })
        {
            var r = await app.Call(TestApp.Mark, "POST", $"projects/{TestApp.Alpha}/mappings", new
            {
                mappingId = "A-MAP-X", businessRole = "SAMPLE X", environment = "Dev", groupReference = "SAMPLE-GRP-X",
                myAccessLink = bad, effectiveFrom = "2026-01-01", sourceReference = "SAMPLE-X",
            });
            Assert.Equal(400, r.Status);
        }
    }

    [Fact]
    public void Snapshot_hash_is_unchanged_when_no_joiner_is_set()
    {
        var s = new RequestSnapshot { RequestId = "r1", ProjectId = TestApp.Alpha, Revision = 1, Role = "SAMPLE Data Engineer", CapturedAt = DateTimeOffset.UnixEpoch };
        var withJoiner = s with { JoinerUpn = Joiner };
        Assert.NotEqual(SnapshotBuilder.Hash(s), SnapshotBuilder.Hash(withJoiner));
        Assert.True(SnapshotBuilder.Verify(withJoiner, SnapshotBuilder.Hash(withJoiner)));
        Assert.False(SnapshotBuilder.Verify(withJoiner with { JoinerUpn = "other@sample.onmicrosoft.com" }, SnapshotBuilder.Hash(withJoiner)));
    }
}
