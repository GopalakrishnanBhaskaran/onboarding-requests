using PmoReview.Adapters;
using PmoReview.Core.Domain;
using PmoReview.Core.Resources;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 F3: Production is read-only by default. Above Read in Prod, nothing is granted — by the agent, a team report, the
/// UI or a forged API call — without an Approved ProdWrite escalation from a ProdApprover who is not the requester (nor an
/// earlier approver), for this request revision and this exact scope.
/// </summary>
public class F3ProdGuardTests
{
    private const string Platform = "SAMPLE PEST Platform (ph)";
    private const string Joiner = "jane.doe@company.example";
    private const string JoinerUpn = "jane.doe@sample.invalid";
    private const string WriterGroup = "sample-azr-pes-rg-data-prd-ph-weu-support-writer";
    private const string ReaderGroup = "sample-azr-pes-rg-data-prd-ph-weu-support-reader";
    private const string Petra = "sample-user-petra"; // ProdApprover (SAMPLE-PEST)
    private const string Reason = "SAMPLE: provides application support for the PEST platform and fixes failed production loads (re-drops, file corrections).";

    private static PilotSettings On(bool provisioning = true) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = provisioning, AgentRoleAssignment = provisioning,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader, "ba92f5b4-2d11-453d-a403-e96b0029c9fe", "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"],
            RoleAssignScopes = [TestApp.SampleAccessScope],
        },
    };

    private static AzureRoleGrant Grant(string name, string id) => new() { RoleName = name, RoleDefinitionId = id, Scope = TestApp.SampleAccessScope, ResourceName = "rg-pmo-sample-access" };

    /// <summary>A Prod writer row (Storage Blob Data Contributor) and the Prod reader row made usable (read-only roles).</summary>
    private static async Task<TestApp> App(bool provisioning = true, string writerLevel = "Support Writer")
    {
        var app = TestApp.With(settings: On(provisioning));
        var writer = app.Directory.AddGroup(WriterGroup);
        var reader = app.Directory.AddGroup(ReaderGroup);
        await app.Stores.MappingSheet.CreateAsync(new MappingRow
        {
            Id = "P-PH-SUPPORT-WRITER-PRD", ProjectId = TestApp.Pest, RowId = "P-PH-SUPPORT-WRITER-PRD", Resource = Platform, AccessLevel = writerLevel,
            EntraGroup = WriterGroup, GroupObjectId = writer.ObjectId, Role = "Support Writer", Environment = "Prod", Domain = "Platform", ResourceGroupType = "PH",
            AzureRoles = [Grant("Storage Blob Data Contributor", "ba92f5b4-2d11-453d-a403-e96b0029c9fe"), Grant("Reader", "acdd72a7-3385-48ef-bd42-f606fba81ae7")],
            SourceReference = "SAMPLE", Version = 1, UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
        var r = (await app.Stores.MappingSheet.GetAsync(TestApp.Pest, "P-PH-SUPPORT-READER-PRD"))!;
        await app.Stores.MappingSheet.ReplaceAsync(r.Value with
        {
            Flag = RowFlag.None, Notes = null, GroupState = GroupState.Exists, GroupObjectId = reader.ObjectId,
            AzureRoles = [Grant("Storage Blob Data Reader", "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"), Grant("Reader", "acdd72a7-3385-48ef-bd42-f606fba81ae7")],
            Version = r.Value.Version + 1,
        }, r.ETag);
        return app;
    }

    private static object Prod(string level = "Support Writer", object? extra = null) => TestApp.Intake(enterpriseId: Joiner, resource: Platform, accessLevel: level,
        environment: "Prod", justification: Reason, first: "Jane", last: "Doe");

    /// <summary>Submit, AI review, Owner and Security approve.</summary>
    private static async Task<string> BothApproved(TestApp app, object? intake = null, string requester = TestApp.Alice)
    {
        var id = await app.SubmitId(intake ?? Prod(), requester, TestApp.Pest);
        await app.RunWorker();
        var reviewed = (await app.Request(id, TestApp.Pest)).Flow!;
        Assert.True(reviewed.Status == RequestStatus.AwaitingOwner, reviewed.LastReason);
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: TestApp.Pest)).Status);
        var sec = await app.Approve(id, TestApp.Sam, project: TestApp.Pest);
        Assert.True(sec.Status == 200, sec.Body);
        return id;
    }

    private static async Task<Escalation> TheEscalation(TestApp app, string id) =>
        Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Pest), e => e.Value.RequestId == id && e.Value.Kind == EscalationKind.ProdWrite).Value;

    private static Task<Resp> Decide(TestApp app, string user, string escalationId, string decision = "Approved", int? revision = 1, string? remarks = null) =>
        app.Call(user, "POST", $"projects/{TestApp.Pest}/escalations/{escalationId}/decision", new { decision, remarks, requestRevision = revision });

    private static async Task Grant(TestApp app, string user, AppRole role)
    {
        var key = UserAccess.MakeId(user, TestApp.Pest);
        var existing = await app.Stores.UserAccess.GetAsync(user, key);
        if (existing is null) await app.Stores.UserAccess.CreateAsync(new UserAccess { Id = key, UserId = user, ProjectId = TestApp.Pest, Roles = [role], IsSample = true });
        else await app.Stores.UserAccess.ReplaceAsync(existing.Value with { Roles = [.. existing.Value.Roles, role] }, existing.ETag);
    }

    [Fact]
    public async Task F3_prod_write_via_the_api_waits_after_both_approvals_and_nothing_is_granted()
    {
        var app = await App();
        var id = await BothApproved(app);
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal((RequestStatus.AwaitingProdApproval, WaitingOn.ProdApprover), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Null(r.Flow.IdTeamTask);
        for (var i = 0; i < 3; i++) await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingProdApproval, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.DoesNotContain(await app.Notifications(id, TestApp.Pest), n => n.Kind == NotificationKind.TeamTask);
        var e = await TheEscalation(app, id);
        Assert.Equal((EscalationStatus.Open, 1, "Prod", WriterGroup), (e.Status, e.RequestRevision, e.Environment, e.Group));
        Assert.Contains("Storage Blob Data Contributor", e.Reason);
        Assert.Contains(await app.Notifications(id, TestApp.Pest), n => n.Kind == NotificationKind.ApprovalRequested && n.Subject.StartsWith("Prod approval needed"));
        Assert.Contains(await app.Stores.AuditEvents.ListAsync(TestApp.Pest), a => a.Value.Action == "escalation-raised");
    }

    [Fact]
    public async Task F3_prod_approver_approval_sends_it_through_the_normal_audited_path()
    {
        var app = await App();
        var id = await BothApproved(app);
        var e = await TheEscalation(app, id);
        var d = await Decide(app, Petra, e.Id);
        Assert.True(d.Status == 200, d.Body);
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal(RequestStatus.WithIdTeam, r.Flow!.Status);
        Assert.Contains(r.Flow.Approvals, a => a is { Stage: ApprovalStage.Prod, Decision: ApprovalDecision.Approved, By: Petra });
        Assert.Contains(r.Flow.Events, ev => ev.Step == "prod-approved");
        Assert.Equal(EscalationStatus.Approved, (await TheEscalation(app, id)).Status);
        await app.RunWorker(); // the agent's own checks run again before it writes
        Assert.Contains($"add-member {JoinerUpn} {WriterGroup}", app.Writer.Calls);
        Assert.Contains(await app.Stores.AuditEvents.ListAsync(TestApp.Pest), a => a.Value.Action == "prod-escalation-approved");
    }

    [Fact]
    public async Task F3_prod_write_via_ui_bundle_is_needs_human_approval_and_escalated()
    {
        var app = await App();
        var res = await app.Call(TestApp.Alice, "GET", $"resources/resolve?projectId={TestApp.Pest}&environment=Prod&resource=PH");
        Assert.True(res.Status == 200, res.Body);
        var status = res.Json.GetProperty("resolvedAccess").EnumerateArray().ToDictionary(a => a.GetProperty("groupName").GetString()!, a => a.GetProperty("status").GetString());
        Assert.Equal("NeedsHumanApproval", status[WriterGroup]);
        Assert.Equal("Auto", status[ReaderGroup]);

        var bundle = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Pest}/requests/bundle", new
        {
            enterpriseId = Joiner, firstName = "Jane", lastName = "Doe", environment = "Prod", justification = Reason, owner = "sample-user-olivia@sample.invalid",
            selectedResources = new[] { "PH" }, groups = new[] { WriterGroup },
        });
        Assert.True(bundle.Status == 201, bundle.Body);
        var id = bundle.Json.GetProperty("requests")[0].GetProperty("requestId").GetString()!;
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: TestApp.Pest)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam, project: TestApp.Pest)).Status);
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingProdApproval, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        Assert.Empty(app.Writer.Calls);
    }

    [Fact]
    public async Task F3_prod_write_via_api_with_a_forged_body_is_still_escalated()
    {
        var app = await App();
        var forged = new
        {
            enterpriseId = Joiner, firstName = "Jane", lastName = "Doe", resource = Platform, accessLevel = "Support Writer", environment = "Prod", justification = Reason,
            owner = "sample-user-olivia@sample.invalid",
            // None of these exist on the intake: they are ignored.
            status = "Completed", flow = new { status = "WithIdTeam" }, approvals = new[] { new { stage = "Prod", decision = "Approved" } },
            escalation = new { status = "Approved" }, prodApproved = true, environmentTag = "Dev", createdBy = Petra,
        };
        var id = await BothApproved(app, forged);
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal(RequestStatus.AwaitingProdApproval, r.Flow!.Status);
        Assert.Equal(TestApp.Alice, r.CreatedBy);
        Assert.DoesNotContain(r.Flow.Approvals, a => a.Stage == ApprovalStage.Prod);
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        // A forged decision: not a ProdApprover (Owner), an unknown escalation id, and a decision on someone else's project.
        var e = await TheEscalation(app, id);
        Assert.Equal(403, (await Decide(app, TestApp.Olivia, e.Id)).Status);
        Assert.Equal(404, (await Decide(app, Petra, "esc-forged")).Status);
        Assert.Equal(404, (await app.Call(Petra, "POST", $"projects/{TestApp.Alpha}/escalations/{e.Id}/decision", new { decision = "Approved", requestRevision = 1 })).Status);
    }

    [Fact]
    public async Task F3_a_row_labelled_read_that_carries_a_writing_role_is_still_escalated()
    {
        var app = await App(writerLevel: "Read"); // a sheet change (or an upload) relabels the Prod writer row as Read
        var id = await BothApproved(app, Prod(level: "Read"));
        Assert.Equal(RequestStatus.AwaitingProdApproval, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        Assert.Contains("Storage Blob Data Contributor", (await TheEscalation(app, id)).Reason);
    }

    [Fact]
    public async Task F3_an_environment_less_writer_row_requested_for_dev_is_still_escalated()
    {
        // A row with no environment applies to every environment, Prod included: choosing Dev never avoids the ProdApprover.
        var app = await App();
        const string group = "sample-azr-pes-rg-data-prd-ph-weu-support-operator";
        var g = app.Directory.AddGroup(group);
        await app.Stores.MappingSheet.CreateAsync(new MappingRow
        {
            Id = "P-PH-SUPPORT-OPERATOR", ProjectId = TestApp.Pest, RowId = "P-PH-SUPPORT-OPERATOR", Resource = Platform, AccessLevel = "Support Operator",
            EntraGroup = group, GroupObjectId = g.ObjectId, Role = "Support Operator", Environment = null, Domain = "Platform", ResourceGroupType = "PH",
            AzureRoles = [Grant("Storage Blob Data Contributor", "ba92f5b4-2d11-453d-a403-e96b0029c9fe")],
            SourceReference = "SAMPLE", Version = 1, UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
        var id = await BothApproved(app, TestApp.Intake(enterpriseId: Joiner, resource: Platform, accessLevel: "Support Operator", environment: "Dev",
            justification: Reason, first: "Jane", last: "Doe"));
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal(("Dev", RequestStatus.AwaitingProdApproval), (r.Environment, r.Flow!.Status));
        Assert.Contains("Storage Blob Data Contributor", (await TheEscalation(app, id)).Reason);
        for (var i = 0; i < 2; i++) await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.True(ProdGuard.AboveReadInProd(new MappingRow
        {
            Id = "x", ProjectId = "p", RowId = "x", Resource = "r", AccessLevel = "Support Operator", EntraGroup = "g", Role = "r",
            AzureRoles = [Grant("Storage Blob Data Contributor", "ba92f5b4-2d11-453d-a403-e96b0029c9fe")], SourceReference = "SAMPLE", Version = 1, UpdatedBy = "t", UpdatedAt = app.Clock.UtcNow,
        }, "Test") is not null);
    }

    [Fact]
    public async Task F3_prod_read_only_is_fulfilled_without_an_escalation()
    {
        var app = await App();
        var id = await BothApproved(app, Prod(level: "Support Reader"));
        Assert.Equal(RequestStatus.WithIdTeam, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        Assert.Empty(await app.Stores.Escalations.ListAsync(TestApp.Pest));
    }

    [Fact]
    public async Task F3_approver_who_is_the_requester_is_refused()
    {
        var app = await App();
        await Grant(app, Petra, AppRole.Requester);
        var id = await BothApproved(app, requester: Petra);
        var e = await TheEscalation(app, id);
        var d = await Decide(app, Petra, e.Id);
        Assert.Equal(403, d.Status);
        Assert.Equal("requester_cannot_approve", d.ErrorCode);
        Assert.Equal(EscalationStatus.Open, (await TheEscalation(app, id)).Status);
    }

    [Fact]
    public async Task F3_approver_who_resubmitted_the_request_is_refused()
    {
        var app = await App();
        await Grant(app, Petra, AppRole.Requester);
        // Alice submits with a justification too short for the level; the review returns it; Petra corrects and resubmits.
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: Joiner, resource: Platform, accessLevel: "Support Writer", environment: "Prod",
            justification: "SAMPLE: support work.", first: "Jane", last: "Doe"), TestApp.Alice, TestApp.Pest);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Returned, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        var view = await app.View(id, TestApp.Alice, TestApp.Pest);
        var put = await app.Call(Petra, "PUT", $"projects/{TestApp.Pest}/requests/{id}", Prod(), view.Json.GetProperty("request").GetProperty("etag").GetString());
        Assert.True(put.Status == 200, put.Body);
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: TestApp.Pest)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam, project: TestApp.Pest)).Status);
        var e = await TheEscalation(app, id);
        var d = await Decide(app, Petra, e.Id, revision: 2);
        Assert.Equal((403, "requester_cannot_approve"), (d.Status, d.ErrorCode));
        Assert.Equal(EscalationStatus.Open, (await TheEscalation(app, id)).Status);
    }

    [Fact]
    public async Task F3_approver_who_gets_the_access_is_refused()
    {
        var app = await App();
        // The request gives Petra the access (the Domain ID is Petra's sign-in name).
        var id = await BothApproved(app, TestApp.Intake(enterpriseId: "sample-user-petra@company.example", resource: Platform, accessLevel: "Support Writer",
            environment: "Prod", justification: Reason, first: "Petra", last: "Sample"));
        Assert.Equal(TestApp.SignIn(Petra), (await app.Request(id, TestApp.Pest)).DomainId);
        var d = await Decide(app, Petra, (await TheEscalation(app, id)).Id);
        Assert.Equal((403, "beneficiary_cannot_approve"), (d.Status, d.ErrorCode));
        Assert.Empty(app.Writer.Calls);
    }

    [Fact]
    public async Task F3_an_earlier_approver_of_the_same_request_cannot_decide_the_prod_stage()
    {
        var app = await App();
        await Grant(app, TestApp.Sam, AppRole.ProdApprover);
        var id = await BothApproved(app);
        var d = await Decide(app, TestApp.Sam, (await TheEscalation(app, id)).Id);
        Assert.Equal((409, "same_approver"), (d.Status, d.ErrorCode));
    }

    [Fact]
    public async Task F3_approval_after_the_request_was_changed_is_stale()
    {
        var app = await App(provisioning: false);
        var id = await BothApproved(app);
        var e = await TheEscalation(app, id);
        // The approver read an older revision.
        var stale = await Decide(app, Petra, e.Id, revision: 0);
        Assert.Equal((409, "stale_escalation"), (stale.Status, stale.ErrorCode));
        // The request changed after the escalation was raised (new revision): the old escalation no longer counts.
        var v = (await app.Stores.Requests.GetAsync(TestApp.Pest, id))!;
        await app.Stores.Requests.ReplaceAsync(v.Value with { Revision = 2 }, v.ETag);
        var old = await Decide(app, Petra, e.Id, revision: 2);
        Assert.Equal((409, "stale_escalation"), (old.Status, old.ErrorCode));
        await app.RunWorker(); // a new escalation for revision 2 is raised
        Assert.Contains(await app.Stores.Escalations.ListAsync(TestApp.Pest), x => x.Value.RequestId == id && x.Value.RequestRevision == 2 && x.Value.Status == EscalationStatus.Open);
    }

    [Fact]
    public async Task F3_an_approved_escalation_for_an_older_revision_never_lets_the_agent_grant()
    {
        var app = await App();
        var id = await BothApproved(app);
        Assert.Equal(200, (await Decide(app, Petra, (await TheEscalation(app, id)).Id)).Status);
        // Afterwards the request is changed under the approval (forged or racing): revision 2, still With ID Team.
        var v = (await app.Stores.Requests.GetAsync(TestApp.Pest, id))!;
        await app.Stores.Requests.ReplaceAsync(v.Value with { Revision = 2 }, v.ETag);
        await app.RunWorker();
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal(RequestStatus.AwaitingProdApproval, r.Flow!.Status);
        Assert.Empty(app.Writer.Calls);
        Assert.Contains(await app.Issues(id, TestApp.Pest), i => i.Kind == IssueKind.ProdWriteBlocked && i.Severity == IssueSeverity.High);
    }

    [Fact]
    public async Task F3_no_code_path_grants_prod_write_without_an_approved_escalation()
    {
        // The request is put at the granting stages directly (a bug or a forged write) without any escalation.
        var app = await App();
        var id = await app.SubmitId(Prod(), TestApp.Alice, TestApp.Pest);
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: TestApp.Pest)).Status);
        var v = (await app.Stores.Requests.GetAsync(TestApp.Pest, id))!;
        var flow = v.Value.Flow!;
        var forgedTask = new TeamTask { Id = $"{id}:id-team:0", Team = AppRole.IdTeam, State = TeamTaskState.Open, Instructions = "x", CreatedAt = app.Clock.UtcNow, SinceAt = app.Clock.UtcNow };
        await app.Stores.Requests.ReplaceAsync(v.Value with
        {
            Flow = (flow with { IdTeamTask = forgedTask }).To(RequestStatus.WithIdTeam, WaitingOn.IdTeam, app.Clock.UtcNow, "forged", "forged", "forged"),
        }, v.ETag);

        // 1. A team report through the API is refused.
        var report = await app.ReportIdTeam(id, app.IdReport(upn: JoinerUpn, group: WriterGroup), project: TestApp.Pest);
        Assert.Equal((409, "prod_write_not_approved"), (report.Status, report.ErrorCode));
        // 2. The agent stops before writing and sends it back to the ProdApprover with a High issue.
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.RoleWriter.Calls);
        Assert.Equal(RequestStatus.AwaitingProdApproval, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        Assert.Contains(await app.Issues(id, TestApp.Pest), i => i.Kind == IssueKind.ProdWriteBlocked);
        // 3. The same at the role team stage.
        v = (await app.Stores.Requests.GetAsync(TestApp.Pest, id))!;
        var roleTask = forgedTask with { Id = $"{id}:role-team:0", Team = AppRole.RoleTeam };
        await app.Stores.Requests.ReplaceAsync(v.Value with
        {
            Flow = (v.Value.Flow! with { RoleTeamTask = roleTask }).To(RequestStatus.WithRoleTeam, WaitingOn.RoleTeam, app.Clock.UtcNow, "forged", "forged", "forged"),
        }, v.ETag);
        Assert.Equal(409, (await app.ReportRoleTeam(id, TestApp.RoleReport(), project: TestApp.Pest)).Status);
        await app.RunWorker();
        Assert.Empty(app.RoleWriter.Calls);
        Assert.Equal(RequestStatus.AwaitingProdApproval, (await app.Request(id, TestApp.Pest)).Flow!.Status);
    }

    [Fact]
    public async Task F3_a_rejected_escalation_closes_the_request_and_grants_nothing()
    {
        var app = await App();
        var id = await BothApproved(app);
        var e = await TheEscalation(app, id);
        Assert.Equal(400, (await Decide(app, Petra, e.Id, "Rejected")).Status); // remarks required
        var d = await Decide(app, Petra, e.Id, "Rejected", remarks: "Use the Support Reader group in Prod; writes go through change management.");
        Assert.True(d.Status == 200, d.Body);
        Assert.Equal(RequestStatus.Rejected, (await app.Request(id, TestApp.Pest)).Flow!.Status);
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        Assert.Equal(409, (await Decide(app, Petra, e.Id)).Status); // decided once
        Assert.Contains(await app.Notifications(id, TestApp.Pest), n => n.Kind == NotificationKind.Rejected);
    }

    [Fact]
    public async Task F3_escalations_are_listed_only_for_roles_that_handle_them()
    {
        var app = await App();
        await BothApproved(app);
        Assert.Equal(200, (await app.Call(Petra, "GET", $"projects/{TestApp.Pest}/escalations")).Status);
        Assert.Equal(200, (await app.Call(TestApp.Sam, "GET", $"projects/{TestApp.Pest}/escalations")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Pest}/escalations")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Pest}/escalations")).Status);
    }

    [Fact]
    public void F3_guard_rules_cover_levels_roles_and_privilege()
    {
        AzureRoleGrant reader = new() { RoleName = "Reader", RoleDefinitionId = "acdd72a7-3385-48ef-bd42-f606fba81ae7" };
        AzureRoleGrant unknown = new() { RoleName = "Custom Role" };
        Assert.Null(ProdGuard.AboveReadInProd("Dev", "Admin", [], true)); // not Prod
        Assert.Null(ProdGuard.AboveReadInProd("prd", "Read", [reader], false));
        Assert.NotNull(ProdGuard.AboveReadInProd("Production", "Write", [reader], false));
        Assert.NotNull(ProdGuard.AboveReadInProd("Prod", "Read", [reader], true)); // privileged
        Assert.NotNull(ProdGuard.AboveReadInProd("Prod", "Read", [reader, unknown], false)); // a role that cannot be shown to be read-only
        Assert.NotNull(ProdGuard.AboveReadInProd("Prod", "Support Reader", [], false)); // job role with nothing to prove it only reads
        Assert.Null(ProdGuard.AboveReadInProd("Prod", "Support Reader", [reader], false));
    }
}
