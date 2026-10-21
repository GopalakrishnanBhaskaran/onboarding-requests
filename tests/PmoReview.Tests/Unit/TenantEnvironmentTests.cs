using PmoReview.Adapters;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// 21 Oct 2026: tenant separation (one project per Entra tenant, e.g. PEST and Vestacy), environments (Dev / PreProd /
/// Prod) and the response document's group → many Azure roles × many resources, modelled on the SAMPLE PEST support
/// reader/writer rows. The agent provisions and assigns only in its own tenant, only after both approvals, and only when
/// every Azure role is resolved and allow-listed; everything else goes to the teams with the reason.
/// </summary>
public class TenantEnvironmentTests
{
    private const string Platform = "SAMPLE PEST Platform (ph)";
    private const string JoinerMail = "jane.doe@company.example";
    private const string JoinerUpn = "jane.doe@sample.invalid";
    private const string DevReaderGroup = "sample-azr-pes-rg-data-dev-ph-weu-support-reader";
    private const string SupportReason = "SAMPLE: provides application support for the PEST platform and reads pipeline data for support triage of failed loads.";
    private const string WriterReason = "SAMPLE: provides application support for the PEST platform and fixes failed loads (re-drops, file corrections).";

    private static PilotSettings On(string? agentTenant = null) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = true, AgentTenantId = agentTenant,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [TestApp.SampleAccessScope],
        },
    };

    private static object Pest(string level = "Support Reader", string? environment = "Dev", string enterpriseId = JoinerMail, string? justification = null) =>
        TestApp.Intake(enterpriseId: enterpriseId, resource: Platform, accessLevel: level, environment: environment,
            justification: justification ?? (level.Contains("Writer") ? WriterReason : SupportReason), first: "Jane", last: "Doe");

    private static async Task<string> Approved(TestApp app, object intake, string project = TestApp.Pest)
    {
        var id = await app.SubmitId(intake, project: project);
        await app.RunWorker();
        var reviewed = (await app.Request(id, project)).Flow!;
        Assert.True(reviewed.Status == RequestStatus.AwaitingOwner, reviewed.LastReason);
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: project)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam, project: project)).Status);
        return id;
    }

    private static async Task<ReviewResult> LatestReview(TestApp app, string id, string project = TestApp.Pest)
    {
        var runs = (await app.Stores.Runs.ListAsync(project)).Select(r => r.Value).Where(r => r.RequestId == id).OrderBy(r => r.QueuedAt).ToList();
        return runs[^1].Result!;
    }

    [Fact]
    public async Task Pest_dev_support_reader_goes_end_to_end_with_the_agent_creating_the_account_and_assigning_the_azure_role()
    {
        var app = TestApp.With(settings: On());
        var id = await app.SubmitId(Pest(environment: "dev"), project: TestApp.Pest);
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal("Dev", r.Environment); // the project's spelling
        Assert.Equal(JoinerUpn, r.DomainId); // local part of the enterprise mail + the tenant domain
        Assert.Contains(await app.Notifications(id, TestApp.Pest), n => n.Kind == NotificationKind.FlowOverview && n.To.Contains(JoinerMail));

        await app.RunWorker();
        var review = await LatestReview(app, id);
        Assert.Equal(ReviewOutcome.Pass, review.Outcome);
        Assert.Contains("tenant SAMPLE PEST", review.Summary);
        Assert.Contains("environment Dev", review.Summary);
        Assert.Contains("Azure roles on the group (1): Reader on rg-pmo-sample-access", review.Summary);
        Assert.Equal(CheckStatus.Pass, review.Checks.Single(c => c.Code == CheckCodes.MapAzureRoles).Status);

        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia, project: TestApp.Pest)).Status);
        Assert.Empty(app.Writer.Calls); // nothing before both approvals
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam, project: TestApp.Pest)).Status);
        Assert.Empty(app.Writer.Calls); // the API holds no writer

        await app.RunWorker();
        Assert.Equal([$"create-user {JoinerUpn}", $"create-group {DevReaderGroup}", $"add-member {JoinerUpn} {DevReaderGroup}"], app.Writer.Calls);
        Assert.Equal(("Jane Doe", "jane.doe"), (app.Writer.Users[0].DisplayName, app.Writer.Users[0].MailNickname));
        r = await app.Request(id, TestApp.Pest);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        var provisioned = Assert.Single(await app.Notifications(id, TestApp.Pest), n => n.Kind == NotificationKind.ProvisioningSummary);
        Assert.Contains("sample.security.pest@sample.invalid", provisioned.To);
        Assert.Contains("sample-user-olivia@sample.invalid", provisioned.To);
        Assert.Contains($"user {JoinerUpn} created", provisioned.Body);

        // The ID team issues the one-time TAP in Entra and records metadata only.
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport(upn: JoinerUpn, group: DevReaderGroup), project: TestApp.Pest)).Status);
        await app.RunWorker();
        Assert.Equal([$"Group {app.Directory.GroupId(DevReaderGroup)} {FakeRoleWriter.Reader} {TestApp.SampleAccessScope}"], app.RoleWriter.Calls);
        r = await app.Request(id, TestApp.Pest);
        Assert.Equal(RequestStatus.Verifying, r.Flow!.Status);
        Assert.Equal(CheckStatus.Pass, r.Flow.RoleTeamTask!.Checks.Single(c => c.Code == VerifyCodes.RoleInAzure).Status);
        var record = (await app.Stores.ResponseDocument.GetAsync(TestApp.Pest, id))!.Value;
        Assert.Equal(("Dev", ResponseState.Assigned), (record.Environment, record.State));
        Assert.Equal([new AssignedRole("Reader", TestApp.SampleAccessScope)], record.AssignedRoles);

        var notes = await app.Notifications(id, TestApp.Pest);
        Assert.Contains(notes, n => n.Kind == NotificationKind.ProvisioningSummary && n.Subject.Contains("assigned 1 Azure role"));
        var ready = Assert.Single(notes, n => n.Kind == NotificationKind.AccessReady);
        Assert.Equal([JoinerMail, "sample-user-alice@sample.invalid"], ready.To);
        foreach (var detail in new[] { JoinerUpn, DevReaderGroup, "Environment: Dev", "Tenant: SAMPLE PEST", "Reader on rg-pmo-sample-access", "never sent by mail" })
            Assert.Contains(detail, ready.Body);

        app.UserCompletesFirstSignIn(JoinerUpn);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Completed, (await app.Request(id, TestApp.Pest)).Flow!.Status);
    }

    [Theory]
    [InlineData(null, 400)]
    [InlineData("QA", 400)]
    [InlineData("ppd", 201)]
    [InlineData("PreProd", 201)]
    public async Task A_tenant_project_needs_one_of_its_environments(string? environment, int status)
    {
        var app = TestApp.Create();
        var res = await app.Submit(Pest(environment: environment), project: TestApp.Pest);
        Assert.Equal(status, res.Status);
        if (status == 201) Assert.Equal("PreProd", res.Request.GetProperty("environment").GetString());
        else Assert.Contains("Environment", res.Body);
    }

    [Fact]
    public async Task A_red_prod_row_is_never_sent_for_approval()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(Pest(environment: "prd"), project: TestApp.Pest);
        await app.RunWorker();
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal(RequestStatus.Returned, r.Flow!.Status);
        var flag = (await LatestReview(app, id)).Checks.Single(c => c.Code == CheckCodes.MapRowFlag);
        Assert.Equal(CheckStatus.Fail, flag.Status);
        Assert.Contains("flagged red", flag.Detail);
        Assert.Contains("IaC", flag.Detail);
        Assert.Contains(RiskFlags.Production, (await LatestReview(app, id)).RiskFlags);
    }

    [Fact]
    public async Task Access_missing_in_an_environment_names_where_it_exists()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(Pest(level: "Support Writer", environment: "Prod"), project: TestApp.Pest);
        await app.RunWorker();
        var exists = (await LatestReview(app, id)).Checks.Single(c => c.Code == CheckCodes.MapRowExists);
        Assert.Equal(CheckStatus.Fail, exists.Status);
        Assert.Contains("exists only in Dev, PreProd", exists.Detail);
        Assert.Contains("No Prod group exists", exists.Detail);
    }

    [Fact]
    public async Task An_amber_row_passes_with_its_caveat_shown_to_the_approvers()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: JoinerMail, resource: "SAMPLE PEST Finance reporting (fin)", accessLevel: "Power BI Developer",
            environment: "PreProd", justification: "SAMPLE: builds and changes the finance Power BI reports for the controllers."), project: TestApp.Pest);
        await app.RunWorker();
        var review = await LatestReview(app, id);
        Assert.Equal(ReviewOutcome.Pass, review.Outcome);
        Assert.Contains(RiskFlags.RowCaveat, review.RiskFlags);
        Assert.Contains("Mapping row note (Amber)", review.Summary);
        var ask = Assert.Single(await app.Notifications(id, TestApp.Pest), n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Contains("powerbippdelopers", ask.Body);
    }

    [Fact]
    public async Task Unresolved_azure_roles_go_to_the_role_team_which_must_record_every_one_exactly()
    {
        var app = TestApp.With(settings: On());
        app.Directory.AddUser(JoinerUpn);
        var id = await Approved(app, Pest(environment: "PreProd"));
        await app.RunWorker(); // existing user + existing group: membership by the agent, verified on the next run
        await app.RunWorker();
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal((RequestStatus.WithRoleTeam, WaitingOn.RoleTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Empty(app.RoleWriter.Calls); // all or nothing: no half assignment
        Assert.Contains("4 of 4 Azure role(s) have no resolved role id or scope", r.Flow.RoleTeamTask!.Agent!.Manual);
        Assert.DoesNotContain(await app.Issues(id, TestApp.Pest), i => i.Kind == IssueKind.NeedsDeveloper); // a normal hand-off
        foreach (var line in new[] { "Storage Blob Data Reader on sample-pesstdppddaiweu01", "Reader on sample-pes-rg-data-ppd-acq-weu-001" })
            Assert.Contains(line, r.Flow.RoleTeamTask.Instructions);

        // The role team assigns in Azure (the fake reader then shows the four assignments) and records what it did.
        var group = app.Directory.GroupId("sample-azr-pes-rg-data-ppd-ph-weu-support-reader");
        foreach (var (role, at) in new[] { ("Storage Blob Data Reader", "sample-pesstdppddaiweu01"), ("Storage Blob Data Reader", "sample-pesstdppdlogweu01"),
                     ("Reader", "sample-pes-rg-data-ppd-dai-weu-001"), ("Reader", "sample-pes-rg-data-ppd-acq-weu-001") })
            app.Roles.Assign(group, role, at);
        var three = new
        {
            assignedRoles = new[]
            {
                new { role = "Storage Blob Data Reader", scope = "sample-pesstdppddaiweu01" }, new { role = "Storage Blob Data Reader", scope = "sample-pesstdppdlogweu01" },
                new { role = "Reader", scope = "sample-pes-rg-data-ppd-dai-weu-001" },
            },
            notes = "SAMPLE",
        };
        Assert.Equal(200, (await app.ReportRoleTeam(id, three, project: TestApp.Pest)).Status);
        await app.RunWorker();
        r = await app.Request(id, TestApp.Pest);
        Assert.Equal(TeamTaskState.Returned, r.Flow!.RoleTeamTask!.State);
        Assert.Contains("missing Reader at sample-pes-rg-data-ppd-acq-weu-001", r.Flow.RoleTeamTask.LastDifference);

        var all = new
        {
            assignedRoles = three.assignedRoles.Append(new { role = "Reader", scope = "sample-pes-rg-data-ppd-acq-weu-001" }).ToArray(),
            notes = "SAMPLE",
        };
        Assert.Equal(200, (await app.ReportRoleTeam(id, all, project: TestApp.Pest)).Status);
        await app.RunWorker();
        Assert.Equal(RequestStatus.Verifying, (await app.Request(id, TestApp.Pest)).Flow!.Status);
    }

    [Fact]
    public async Task A_role_outside_the_allow_list_leaves_the_whole_assignment_to_the_role_team_with_a_high_issue()
    {
        var app = TestApp.With(settings: On());
        var id = await Approved(app, Pest(level: "Support Writer"));
        await app.RunWorker();
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport(upn: JoinerUpn, group: "sample-azr-pes-rg-data-dev-ph-weu-support-writer"), project: TestApp.Pest)).Status);
        await app.RunWorker();
        var r = await app.Request(id, TestApp.Pest);
        Assert.Equal((RequestStatus.WithRoleTeam, WaitingOn.RoleTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Empty(app.RoleWriter.Calls); // not even the allowed Reader line
        Assert.Contains("not on the agent's allow-list", r.Flow.RoleTeamTask!.Agent!.Manual);
        Assert.Contains("Storage Blob Data Contributor", r.Flow.RoleTeamTask.Agent.Manual);
        Assert.Contains(await app.Issues(id, TestApp.Pest), i => i is { Kind: IssueKind.NeedsDeveloper, Severity: IssueSeverity.High });
    }

    [Fact]
    public async Task A_project_in_another_tenant_is_never_read_or_changed_by_the_agent()
    {
        var app = TestApp.With(settings: On(agentTenant: "11111111-2222-4333-8444-555555555555"));
        var intake = TestApp.Intake(enterpriseId: "ved.engineer@company.example", resource: "SAMPLE Vestacy Platform (ph)", accessLevel: "Data Engineer",
            environment: "Dev", justification: "SAMPLE: joins the platform team to build and fix the ingestion pipelines in dev.");
        var reads = app.Directory.Reads;
        var id = await Approved(app, intake, TestApp.Vestacy);
        var review = await LatestReview(app, id, TestApp.Vestacy);
        Assert.All(review.Checks.Where(c => c.Group == CheckGroups.EntraCrossCheck), c => Assert.Equal(CheckStatus.NotApplicable, c.Status));
        Assert.Contains(RiskFlags.TenantNotConnected, review.RiskFlags);
        Assert.Equal(reads, app.Directory.Reads); // no Entra read for another tenant

        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);
        var r = await app.Request(id, TestApp.Vestacy);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.IdTeam), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Contains("tenant SAMPLE Vestacy is separate from the AI agent's tenant", r.Flow.IdTeamTask!.Agent!.Manual);

        // The ID team works in the Vestacy tenant and records it; the agent verifies against the report, not Entra.
        Assert.Equal(200, (await app.ReportIdTeam(id, app.IdReport(upn: "ved.engineer@sample-vestacy.invalid", group: "sample-azr-ves-rg-data-dev-ph-weu-dataengineers", userCreated: true),
            project: TestApp.Vestacy)).Status);
        await app.RunWorker();
        r = await app.Request(id, TestApp.Vestacy);
        Assert.Equal(RequestStatus.WithRoleTeam, r.Flow!.Status);
        Assert.Contains(r.Flow.IdTeamTask!.Checks, c => c.Code == VerifyCodes.EntraUser && c.Status == CheckStatus.NotApplicable && c.Detail.Contains("SAMPLE Vestacy"));
        Assert.Contains("separate from the AI agent's tenant", r.Flow.RoleTeamTask!.Agent!.Manual);
        Assert.Equal(reads, app.Directory.Reads);
        Assert.Empty(app.RoleWriter.Calls);
    }

    [Fact]
    public async Task A_project_bound_to_the_agents_own_tenant_is_provisioned()
    {
        const string tenant = "11111111-2222-4333-8444-555555555555";
        var app = TestApp.With(settings: On(agentTenant: tenant));
        var p = (await app.Stores.Projects.GetAsync(TestApp.Pest, TestApp.Pest))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = tenant }, p.ETag);
        var id = await Approved(app, Pest());
        await app.RunWorker();
        Assert.Contains($"create-user {JoinerUpn}", app.Writer.Calls);
        Assert.Equal(RequestStatus.WithIdTeam, (await app.Request(id, TestApp.Pest)).Flow!.Status);
    }

    [Theory]
    [InlineData("owner@contoso.com", "owner@contoso.com", true)]
    [InlineData("Owner@Contoso.com ", "owner@contoso.com", true)]
    [InlineData("owner_contoso.com#EXT#@pilot.onmicrosoft.com", "owner@contoso.com", true)]
    [InlineData("live.com#owner@gmail.com", "owner@gmail.com", true)]
    [InlineData("owner2@contoso.com", "owner@contoso.com", false)]
    [InlineData("owner_contoso.com#EXT#@pilot.onmicrosoft.com", "owner_other.com@contoso.com", false)]
    public void The_named_owner_is_recognised_under_each_form_of_the_same_sign_in(string signedInAs, string named, bool same) =>
        Assert.Equal(same, new CallerContext("u1", signedInAs, new Dictionary<string, IReadOnlySet<AppRole>>()).Is(named));

    [Fact]
    public async Task A_guest_owner_signed_in_with_the_guest_upn_sees_and_gives_the_owner_decision()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(owner: "olivia@partner.example"));
        await app.RunWorker();
        var path = $"projects/{TestApp.Alpha}/requests/{id}";
        var view = await app.CallAs(TestApp.Olivia, "olivia_partner.example#EXT#@pilot.onmicrosoft.com", "GET", path);
        Assert.Contains("ApproveOwner", view.Json.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        var run = (await app.Request(id)).Flow!.Scope!.ReviewRunId;
        var res = await app.CallAs(TestApp.Olivia, "olivia_partner.example#EXT#@pilot.onmicrosoft.com", "POST", $"{path}/approvals",
            new { decision = "Approved", remarks = (string?)null, reviewRunId = run });
        Assert.Equal(200, res.Status);
        Assert.Equal(RequestStatus.AwaitingSecurity, await app.Status(id));
    }

    [Fact]
    public async Task Someone_who_cannot_decide_is_told_why_instead_of_seeing_a_blank_page()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId(TestApp.Intake(owner: "sample-user-otto@sample.invalid"));
        await app.RunWorker();
        // Olivia holds the Owner role but is not the Owner named on this request.
        var hint = (await app.View(id, TestApp.Olivia)).Json.GetProperty("approvalHint").GetString();
        Assert.Contains("Waiting for the Owner named on the request: sample-user-otto@sample.invalid", hint);
        Assert.Contains("You are signed in as sample-user-olivia@sample.invalid", hint);
        // The requester just sees who it waits for.
        Assert.Equal("Waiting for the Owner named on the request: sample-user-otto@sample.invalid.", (await app.View(id)).Json.GetProperty("approvalHint").GetString());

        var named = await app.SubmitId(TestApp.Intake(owner: "sample-user-alice@sample.invalid", enterpriseId: "sample.e1009"));
        await app.RunWorker();
        // Alice is named but has no Owner role: told to get it registered.
        Assert.Contains("has no Owner role in this project yet", (await app.View(named)).Json.GetProperty("approvalHint").GetString());
        // The named Owner with the role gets the decision, not a hint.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await app.View(id, "sample-user-otto")).Json.GetProperty("approvalHint").ValueKind);
    }

    /// <summary>tools/import_workbook.py output (tests/fixtures/import-sample.json, from tools/test_import_workbook.py) is read by the real model.</summary>
    [Fact]
    public async Task Workbook_import_rows_load_into_the_mapping_sheet_and_drive_the_review()
    {
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "import-sample.json")));
        var rows = json.RootElement.GetProperty("mappingSheet").EnumerateArray()
            .Select(e => PmoReview.Core.Common.AppJson.Deserialize<MappingRow>(e.GetRawText())! with { ProjectId = TestApp.Pest, Id = e.GetProperty("rowId").GetString()! })
            .ToList();
        Assert.Contains(rows, r => r is { Flag: RowFlag.Red, Environment: "Prod", GroupState: GroupState.ToBeCreated });
        var app = TestApp.Create();
        var p = (await app.Stores.Projects.GetAsync(TestApp.Pest, TestApp.Pest))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { GroupNamePattern = "^azr-pes-rg-data-(dev|tst|ppd|prd)-[a-z0-9]+-weu-[A-Za-z0-9-]+$" }, p.ETag);
        foreach (var r in rows) await app.Stores.MappingSheet.CreateAsync(r);

        var dev = await app.SubmitId(TestApp.Intake(enterpriseId: JoinerMail, resource: "SAMPLE PEST Central hub / platform (ph)", accessLevel: "Support Reader",
            environment: "Dev", justification: SupportReason), project: TestApp.Pest);
        var prod = await app.SubmitId(TestApp.Intake(enterpriseId: JoinerMail, resource: "SAMPLE PEST Central hub / platform (ph)", accessLevel: "Support Reader",
            environment: "Prod", justification: SupportReason), project: TestApp.Pest);
        await app.RunWorker();
        var review = await LatestReview(app, dev);
        Assert.True(review.Outcome == ReviewOutcome.Pass, string.Join(" | ", review.ReturnReasons));
        Assert.Contains("Azure roles on the group (3)", review.Summary);
        Assert.Contains(RiskFlags.RowCaveat, review.RiskFlags);
        Assert.Equal(RequestStatus.Returned, (await app.Request(prod, TestApp.Pest)).Flow!.Status);
    }

    [Fact]
    public async Task Pmo_saves_the_response_document_roles_of_a_row_and_built_in_names_resolve_to_their_ids()
    {
        var app = TestApp.Create();
        var path = $"projects/{TestApp.Pest}/mapping-sheet/P-FIN-PBI-DEV-PPD";
        var sheet = await app.Call(TestApp.Paul, "GET", $"projects/{TestApp.Pest}/mapping-sheet");
        var item = sheet.Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("value").GetProperty("rowId").GetString() == "P-FIN-PBI-DEV-PPD");
        var v = item.GetProperty("value");
        object Body(object roles) => new
        {
            resource = v.GetProperty("resource").GetString(), accessLevel = v.GetProperty("accessLevel").GetString(), environment = "PreProd",
            entraGroup = v.GetProperty("entraGroup").GetString(), role = v.GetProperty("role").GetString(), sourceReference = "SAMPLE response document",
            groupState = "Exists", flag = "Amber", notes = "SAMPLE caveat", azureRoles = roles,
        };
        var bad = await app.Call(TestApp.Paul, "PUT", path, Body(new[] { new { roleName = "Reader", scope = "rg-x" } }), item.GetProperty("etag").GetString());
        Assert.Equal(400, bad.Status);
        var ok = await app.Call(TestApp.Paul, "PUT", path, Body(new object[]
        {
            new { roleName = "Reader", resourceName = "rg-pmo-sample-access", scope = TestApp.SampleAccessScope },
            new { roleName = "Storage Blob Data Reader", resourceName = "sample-pesstdppddaiweu01" },
        }), item.GetProperty("etag").GetString());
        Assert.Equal(200, ok.Status);
        var row = (await app.Stores.MappingSheet.GetAsync(TestApp.Pest, "P-FIN-PBI-DEV-PPD"))!.Value;
        Assert.Equal([FakeRoleWriter.Reader, "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"], row.AzureRoles.Select(g => g.RoleDefinitionId));
        Assert.Equal(("PreProd", RowFlag.Amber, 2), (row.Environment, row.Flag, row.Version));
    }
}
