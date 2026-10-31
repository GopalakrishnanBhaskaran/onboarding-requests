using Microsoft.Extensions.Logging;
using System.Text.Json;
using PmoReview.Adapters;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Offboarding;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 F5: E2E offboarding — plan (dry run) → explicit confirm (a second person in a production tenant) → execute in order
/// → verify → audit, all Graph and ARM calls mocked. Only the PMO is told, and only when nothing is left. Each F5 edge case
/// of the feature pack (B.8) is one test.
/// </summary>
public class F5OffboardingTests
{
    private const string Upn = "sample.leaver@sample.invalid";
    private const string SubA = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string SubB = "bbbbbbbb-0000-4000-8000-000000000002";
    private const string Pmo = "sample.pmo.alpha@sample.invalid";
    private const string GroupA = "SAMPLE-GRP-ALPHA-SALES-READ";
    private const string GroupB = "SAMPLE-GRP-ALPHA-DBW-ENG-USER";

    private const string AgentTenant = "00000000-0000-4000-8000-0000000000a1"; // SAMPLE tenant id

    private static PilotSettings Settings(bool on = true, bool delete = false) =>
        new() { Workflow = new WorkflowSettings { AgentOffboarding = on, OffboardDeleteUser = delete, AgentTenantId = AgentTenant } };

    /// <summary>The project names the agent's own tenant (offboarding is only for such a project) and its production flag.</summary>
    private static async Task OwnTenant(TestApp app, string project = TestApp.Alpha, bool? production = false)
    {
        var p = (await app.Stores.Projects.GetAsync(project, project))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { TenantId = AgentTenant, IsProductionTenant = production }, p.ETag);
    }

    private static Task AddPerson(TestApp app, string upn, string project = TestApp.Alpha, bool retired = false) =>
        app.Stores.PeopleInventory.CreateAsync(new Person
        {
            Id = Person.MakeId(upn), ProjectId = project, EnterpriseId = upn, EnterpriseEmail = upn, DisplayName = "SAMPLE Leaver", Domain = "Sales",
            Retired = retired, UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });

    /// <summary>A leaver with two direct groups, one nested group, a TAP, one Azure role, an inventory entry and an open request.</summary>
    private static async Task<(TestApp App, string RequestId)> Leaver(bool on = true, bool? production = false)
    {
        var app = TestApp.With(settings: Settings(on));
        await OwnTenant(app, production: production);
        var user = app.Directory.AddUser(Upn);
        app.Directory.AddMember(GroupA, Upn);
        app.Directory.AddMember(GroupB, Upn);
        app.Offboarding.Nested[Upn] = ["SAMPLE-GRP-ALPHA-LEGACY-USER"]; // through another group: goes with it
        app.Offboarding.Taps[Upn] = [Guid.NewGuid().ToString("D")];
        app.Offboarding.Assign(SubA, user.ObjectId);
        await AddPerson(app, Upn);
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.leaver@company.example"));
        Assert.Equal(Upn, (await app.Request(id)).DomainId);
        return (app, id);
    }

    private static Task<Resp> Plan(TestApp app, string upn = Upn, string user = TestApp.Paul, string project = TestApp.Alpha) =>
        app.Call(user, "POST", $"offboarding/{Uri.EscapeDataString(upn)}?projectId={project}&mode=plan");

    private static Task<Resp> Execute(TestApp app, string offboardingId, string upn = Upn, string user = TestApp.Oscar, string project = TestApp.Alpha) =>
        app.Call(user, "POST", $"offboarding/{Uri.EscapeDataString(upn)}?projectId={project}&mode=execute", new { offboardingId });

    private static Task<Resp> Get(TestApp app, string upn = Upn, string user = TestApp.Paul) =>
        app.Call(user, "GET", $"offboarding/{Uri.EscapeDataString(upn)}?projectId={TestApp.Alpha}");

    private static string Status(Resp r) => r.Json.GetProperty("status").GetString()!;
    private static string IdOf(Resp r) => r.Json.GetProperty("offboardingId").GetString()!;
    private static List<(string Name, string Target, string Result, string Detail)> Steps(Resp r) =>
        r.Json.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("name").GetString()!, s.GetProperty("target").GetString()!,
            s.GetProperty("result").GetString()!, s.GetProperty("detail").GetString()!)).ToList();

    /// <summary>Plan → worker → Planned.</summary>
    private static async Task<Resp> Planned(TestApp app, string upn = Upn, string user = TestApp.Paul)
    {
        var r = await Plan(app, upn, user);
        Assert.True(r.Status == 202, r.Body);
        Assert.Equal("Planning", Status(r));
        await app.RunWorker();
        return await Get(app, upn);
    }

    private static async Task<IReadOnlyList<Notification>> OffboardingMails(TestApp app) =>
        (await app.Notifications(project: TestApp.Alpha)).Where(n => n.Kind == NotificationKind.OffboardingCompleted).ToList();

    [Fact]
    public async Task F5_Plan_confirm_execute_verify_audit_in_order_and_only_the_pmo_is_told()
    {
        var (app, requestId) = await Leaver();
        var planned = await Planned(app);
        Assert.Equal(["offboardingId", "enterpriseId", "objectId", "mode", "status", "steps", "leftovers", "notCovered", "pmoNotified", "correlationId"],
            planned.Json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("plan", "Planned", Upn, false), (planned.Json.GetProperty("mode").GetString(), Status(planned), planned.Json.GetProperty("enterpriseId").GetString(), planned.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Equal(app.Directory.User(Upn)!.ObjectId, planned.Json.GetProperty("objectId").GetString());
        Assert.Equal(["name", "target", "action", "result", "detail"], planned.Json.GetProperty("steps")[0].EnumerateObject().Select(p => p.Name));
        // The dry run lists everything and changes nothing.
        Assert.Equal([OffboardingJob.Resolve, OffboardingJob.Revoke, OffboardingJob.Tap, OffboardingJob.Membership, OffboardingJob.Membership, OffboardingJob.Rbac,
            OffboardingJob.Disable, OffboardingJob.Delete, OffboardingJob.Leaver, OffboardingJob.Close], Steps(planned).Select(s => s.Name));
        Assert.All(Steps(planned).Skip(1), s => Assert.Contains(s.Result, new[] { "Planned", "Skipped" }));
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.Delete && s.Result == "Skipped" && s.Detail.Contains("OFFBOARD_DELETE_USER=false"));
        Assert.DoesNotContain(Steps(planned), s => s.Detail.Contains("SAMPLE-GRP-ALPHA-LEGACY-USER")); // nested: goes with the direct one
        Assert.Contains(planned.Json.GetProperty("notCovered").EnumerateArray(), n => n.GetString()!.StartsWith("Databricks workspace users"));
        Assert.Contains(planned.Json.GetProperty("notCovered").EnumerateArray(), n => n.GetString()!.StartsWith("App role assignments"));
        Assert.Empty(app.Offboarding.Calls);
        Assert.True(app.Directory.User(Upn)!.AccountEnabled);

        var confirmed = await Execute(app, IdOf(planned));
        Assert.Equal((202, "Executing", "execute"), (confirmed.Status, Status(confirmed), confirmed.Json.GetProperty("mode").GetString()));
        await app.RunWorker();

        var done = await Get(app);
        Assert.Equal(("Completed", true), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Empty(done.Json.GetProperty("leftovers").EnumerateArray());
        // In order: sessions → TAP → memberships → Azure role assignments → disable.
        var calls = app.Offboarding.Calls;
        int First(string prefix) => calls.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));
        int Last(string prefix) => calls.FindLastIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Equal(6, calls.Count);
        Assert.True(First("revoke") < First("delete-tap") && First("delete-tap") < First("remove-member") && Last("remove-member") < First("delete-role")
            && First("delete-role") < First("disable"), string.Join(" | ", calls));
        Assert.StartsWith("delete-role /subscriptions/" + SubA, calls[First("delete-role")]);
        Assert.Empty(app.Directory.GroupsOf(Upn));
        Assert.False(app.Directory.User(Upn)!.AccountEnabled);
        Assert.Equal(PersonStatus.Leaver, (await app.Stores.PeopleInventory.GetAsync(TestApp.Alpha, Person.MakeId(Upn)))!.Value.Status);
        var request = (await app.Request(requestId)).Flow!;
        Assert.Equal(RequestStatus.Closed, request.Status);
        Assert.Contains(request.Events, e => e.Step == "offboarded");

        // One mail, to the PMO only, with exactly the agreed text; nothing to the user, the manager or anyone else.
        var mail = Assert.Single(await OffboardingMails(app));
        Assert.Equal([Pmo], mail.To);
        Assert.Equal($"Offboarding completed for SAMPLE Leaver ({Upn}) at {Messages.Time(app.Clock.UtcNow)}. Reference {done.Json.GetProperty("correlationId").GetString()}.", mail.Body);
        var audit = (await app.Stores.AuditEvents.ListAsync(TestApp.Alpha)).Select(a => a.Value.Action).ToList();
        foreach (var a in new[] { "offboarding-plan-requested", "offboarding-planned", "offboarding-confirmed", "offboarding-step", "offboarding-completed" })
            Assert.Contains(a, audit);
    }

    [Fact]
    public async Task F5_User_not_found_stops_with_nothing_changed()
    {
        var (app, _) = await Leaver();
        await AddPerson(app, "sample.nobody@sample.invalid"); // in the people inventory, not in Entra
        var planned = await Planned(app, "sample.nobody@sample.invalid");
        Assert.Equal("Failed", Status(planned));
        var step = Assert.Single(Steps(planned));
        Assert.Equal((OffboardingJob.Resolve, "Failed"), (step.Name, step.Result));
        Assert.Equal("", planned.Json.GetProperty("objectId").GetString());
        var execute = await Execute(app, IdOf(planned), "sample.nobody@sample.invalid");
        Assert.Equal((200, "Failed"), (execute.Status, Status(execute))); // a finished run is returned as it is
        await app.RunWorker();
        Assert.Empty(app.Offboarding.Calls);
        Assert.Empty(await OffboardingMails(app));
    }

    [Fact]
    public async Task F5_Two_users_with_the_same_display_name_only_the_named_enterprise_id_is_offboarded()
    {
        var (app, _) = await Leaver();
        const string twin = "sample.leaver2@sample.invalid";
        app.Directory.AddUser(twin);
        app.Directory.AddMember(GroupA, twin);
        await AddPerson(app, twin);
        var planned = await Planned(app);
        Assert.Equal(app.Directory.User(Upn)!.ObjectId, planned.Json.GetProperty("objectId").GetString());
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        Assert.Equal("Completed", Status(await Get(app)));
        Assert.True(app.Directory.User(twin)!.AccountEnabled);
        Assert.Contains(GroupA, app.Directory.GroupsOf(twin));
        Assert.DoesNotContain(app.Offboarding.Calls, c => c.Contains(twin, StringComparison.Ordinal));
        Assert.Equal(PersonStatus.Active, (await app.Stores.PeopleInventory.GetAsync(TestApp.Alpha, Person.MakeId(twin)))!.Value.Status);
    }

    [Fact]
    public async Task F5_Already_disabled_account_is_not_patched_again()
    {
        var (app, _) = await Leaver();
        app.Directory.SetEnabled(Upn, false);
        var planned = await Planned(app);
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.Disable && s.Result == "Skipped" && s.Detail == "already disabled");
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        Assert.Equal("Completed", Status(await Get(app)));
        Assert.DoesNotContain(app.Offboarding.Calls, c => c.StartsWith("disable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F5_Partial_failure_midway_then_rerun_changes_nothing_twice()
    {
        var (app, _) = await Leaver();
        var planned = await Planned(app);
        app.Offboarding.FailOnce = $"remove-member {Upn} {GroupA}"; // Graph unavailable for the second group
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        var paused = await Get(app);
        Assert.Equal("Executing", Status(paused));
        Assert.Contains(Steps(paused), s => s.Name == OffboardingJob.Membership && s.Target == app.Directory.GroupId(GroupA) && s.Result == "Planned");
        Assert.Contains(Steps(paused), s => s.Name == OffboardingJob.Revoke && s.Result == "Done");
        Assert.True(app.Directory.User(Upn)!.AccountEnabled); // the rest waits, in order

        await app.RunWorker();
        Assert.Equal("Completed", Status(await Get(app)));
        Assert.Single(app.Offboarding.Calls, c => c == $"revoke {Upn}");
        Assert.Single(app.Offboarding.Calls, c => c == $"remove-member {Upn} {GroupB}");
        Assert.Single(app.Offboarding.Calls, c => c == $"remove-member {Upn} {GroupA}");
        Assert.Single(app.Offboarding.Calls, c => c.StartsWith("delete-tap", StringComparison.Ordinal));
        await app.RunWorker();
        Assert.Single(await OffboardingMails(app)); // once
    }

    [Fact]
    public async Task F5_Something_removed_by_hand_after_the_plan_is_read_first_and_not_removed_again()
    {
        // Each step reads before it writes: what an administrator (or a lost save) already did counts as done.
        var (app, _) = await Leaver();
        var planned = await Planned(app);
        app.Directory.RemoveMember(GroupB, Upn);
        app.Offboarding.Taps[Upn].Clear();
        var role = app.Offboarding.Assignments[SubA].Single();
        app.Offboarding.Assignments[SubA].Clear();
        app.Directory.SetEnabled(Upn, false);
        await Execute(app, IdOf(planned));
        await app.RunWorker();

        var done = await Get(app);
        Assert.Equal("Completed", Status(done));
        Assert.Equal([$"revoke {Upn}", $"remove-member {Upn} {GroupA}"], app.Offboarding.Calls);
        Assert.Contains(Steps(done), s => s.Target == app.Directory.GroupId(GroupB) && s.Result == "Done" && s.Detail.EndsWith("already removed", StringComparison.Ordinal));
        Assert.Contains(Steps(done), s => s.Name == OffboardingJob.Tap && s.Detail == "already removed");
        Assert.Contains(Steps(done), s => s.Target == role.Id && s.Detail.EndsWith("already removed", StringComparison.Ordinal));
        Assert.Contains(Steps(done), s => s.Name == OffboardingJob.Disable && s.Detail == "already disabled");
    }

    [Fact]
    public async Task F5_No_RBAC_rights_on_one_subscription_is_a_leftover_and_the_rest_is_done()
    {
        var (app, _) = await Leaver();
        app.Offboarding.Unreadable.Add(SubB);
        var planned = await Planned(app);
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.ReadRbac && s.Target == $"/subscriptions/{SubB}" && s.Result == "Skipped");
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains(SubB));
        Assert.Contains(app.Offboarding.Calls, c => c.StartsWith($"delete-role /subscriptions/{SubA}", StringComparison.Ordinal));
        Assert.False(app.Directory.User(Upn)!.AccountEnabled);
        Assert.Empty(await OffboardingMails(app));
        Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), e => e.Value.Kind == EscalationKind.OffboardingLeftover && e.Value.User == Upn);
    }

    /// <summary>
    /// Live, 30 Oct 2026: the membership removal succeeded, but the verify in the same run still saw it (the direct member
    /// check is an advanced query on a separate index store, F104). The agent looks again on the next run and completes.
    /// </summary>
    [Fact]
    public async Task F5_A_removal_Entra_shows_late_is_checked_again_and_the_run_completes()
    {
        var (app, _) = await Leaver();
        app.Offboarding.LagReads = 1;
        var planned = await Planned(app);
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        Assert.Equal("Executing", Status(await Get(app))); // not a leftover yet: looked again on the next run
        Assert.Empty(await app.Stores.Escalations.ListAsync(TestApp.Alpha));
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal(("Completed", true), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Equal(6, app.Offboarding.Calls.Count); // nothing was written twice
        Assert.Single(await OffboardingMails(app));
    }

    /// <summary>Still shown after three looks (about 10 minutes): a leftover like any other, with its escalation and no mail.</summary>
    [Fact]
    public async Task F5_A_removal_still_shown_after_three_looks_is_a_leftover()
    {
        var (app, _) = await Leaver();
        app.Offboarding.LagReads = 100;
        var planned = await Planned(app);
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        await app.RunWorker();
        Assert.Equal("Executing", Status(await Get(app)));
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString() == $"still in {GroupA}");
        Assert.Empty(await OffboardingMails(app));
        Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), e => e.Value.Kind == EscalationKind.OffboardingLeftover);
    }

    /// <summary>
    /// Option 3 "Entra only" (Gokul, 30 Oct 2026): the worker has no Azure right and OFFBOARD_SUBSCRIPTIONS is empty, so it
    /// lists no subscription. It cannot say no Azure role is left: a leftover, never Completed, no PMO mail.
    /// </summary>
    [Fact]
    public async Task F5_No_subscription_visible_is_a_leftover_and_never_completed()
    {
        var (app, _) = await Leaver();
        app.Offboarding.Assignments.Clear();
        var planned = await Planned(app);
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.ReadRbac && s.Target == "/subscriptions" && s.Result == "Skipped");
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains("can see no Azure subscription"));
        Assert.False(app.Directory.User(Upn)!.AccountEnabled); // the Entra steps are done
        Assert.Empty(await OffboardingMails(app));
        Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), e => e.Value.Kind == EscalationKind.OffboardingLeftover && e.Value.User == Upn);
    }

    [Fact]
    public async Task F5_Requester_equals_confirmer_in_a_production_tenant_is_refused()
    {
        var (app, _) = await Leaver(production: true);
        var planned = await Planned(app, user: TestApp.Paul);
        var same = await Execute(app, IdOf(planned), user: TestApp.Paul);
        Assert.Equal((403, "requester_cannot_confirm"), (same.Status, same.ErrorCode));
        await app.RunWorker();
        Assert.Empty(app.Offboarding.Calls);
        Assert.Equal(202, (await Execute(app, IdOf(planned), user: TestApp.Oscar)).Status); // a second person confirms

        // A tenant never marked counts as production (fails closed): a second person confirms there too.
        var (unset, _) = await Leaver(production: null);
        var u = await Planned(unset, user: TestApp.Paul);
        Assert.Equal((403, "requester_cannot_confirm"), (await Execute(unset, IdOf(u), user: TestApp.Paul)).Pipe(x => (x.Status, x.ErrorCode)));

        // Only in a tenant marked as not production may the same person confirm.
        var (dev, _) = await Leaver(production: false);
        var p = await Planned(dev, user: TestApp.Paul);
        Assert.Equal(202, (await Execute(dev, IdOf(p), user: TestApp.Paul)).Status);
    }

    [Fact]
    public async Task F5_PMO_contact_missing_completes_without_a_mail_and_says_so()
    {
        var (app, _) = await Leaver();
        var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
        await app.Stores.Projects.ReplaceAsync(p.Value with { Contacts = p.Value.Contacts with { Pmo = [] } }, p.ETag);
        var planned = await Planned(app);
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal(("Completed", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Empty(await OffboardingMails(app));
        Assert.Contains(await app.Issues(project: TestApp.Alpha), i => i.Severity == IssueSeverity.High && i.Summary.Contains("no PMO address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F5_Verify_finds_leftovers_so_no_completion_mail()
    {
        var (app, _) = await Leaver();
        app.Directory.AddMember("SAMPLE-GRP-ALPHA-DRIVE-EDIT", Upn);
        app.Offboarding.Dynamic.Add("SAMPLE-GRP-ALPHA-DRIVE-EDIT");                    // membership by rule: cannot be removed
        var forbidden = app.Offboarding.Assign(SubB, app.Directory.User(Upn)!.ObjectId);
        app.Offboarding.DeleteForbidden.Add(forbidden.Id);                              // Azure refuses the delete
        var planned = await Planned(app);
        Assert.Equal(1, Steps(planned).Count(s => s.Name == OffboardingJob.Membership && s.Result == "Skipped"));
        await Execute(app, IdOf(planned));
        await app.RunWorker();

        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        var leftovers = done.Json.GetProperty("leftovers").EnumerateArray().Select(l => l.GetString()!).ToList();
        Assert.Contains(leftovers, l => l.Contains("SAMPLE-GRP-ALPHA-DRIVE-EDIT") && l.Contains("dynamic"));
        Assert.Contains(leftovers, l => l.Contains("Reader") && l.Contains("still assigned"));
        Assert.Contains(Steps(done), s => s.Name == OffboardingJob.Rbac && s.Result == "Failed" && s.Detail.Contains("ArmForbidden"));
        Assert.Empty(await OffboardingMails(app));
        var e = Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), x => x.Value.Kind == EscalationKind.OffboardingLeftover).Value;
        Assert.Contains("No completion mail was sent", e.Reason);
        Assert.False(app.Directory.User(Upn)!.AccountEnabled); // everything that could be done was done
    }

    [Fact]
    public async Task F5_Only_the_projects_own_people_are_offboarded()
    {
        var (app, _) = await Leaver();
        // A PMO of another project (Bella, SAMPLE-BETA) never reaches an ALPHA person: BETA names no tenant, then not their person.
        var beta = await Plan(app, user: TestApp.Bella, project: TestApp.Beta);
        Assert.Equal((409, "tenant_not_connected"), (beta.Status, beta.ErrorCode));
        await OwnTenant(app, TestApp.Beta);
        Assert.Equal(404, (await Plan(app, user: TestApp.Bella, project: TestApp.Beta)).Status);
        // In ALPHA: not in the people inventory and no request → 404; a retired person → 404; another domain → 404.
        const string outsider = "sample.admin@sample.invalid", retired = "sample.retired@sample.invalid";
        app.Directory.AddUser(outsider);
        app.Directory.AddUser(retired);
        await AddPerson(app, retired, retired: true);
        Assert.Equal(404, (await Plan(app, outsider)).Status);
        Assert.Equal(404, (await Plan(app, retired)).Status);
        Assert.Equal(404, (await Plan(app, "sample.leaver@other.invalid")).Status);
        // A person known only from a request of the project: only a request its Owner and Security approved counts (any
        // Requester can write a request for anyone).
        const string joiner = "sample.joiner@sample.invalid";
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.joiner@company.example"));
        Assert.Equal(404, (await Plan(app, joiner)).Status);
        await app.RunWorker();
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        app.Directory.AddUser(joiner);
        var j = await Planned(app, joiner);
        Assert.Equal("Planned", Status(j));
        Assert.Empty(await app.Stores.OffboardingRuns.ListAsync(TestApp.Beta));
        await Execute(app, IdOf(j), joiner);
        await app.RunWorker();
        Assert.False(app.Directory.User(joiner)!.AccountEnabled);
        // Offboarded without a people inventory entry: no new request until the Data Steward adds a rehire as Active.
        var again = await app.Submit(TestApp.Intake(enterpriseId: "sample.joiner@company.example"));
        Assert.Equal((409, "person_is_leaver"), (again.Status, again.ErrorCode));

        // A run that reaches the store some other way is refused by the worker too, and nothing is changed.
        await app.Stores.OffboardingRuns.CreateAsync(new OffboardingRun
        {
            Id = "off-forged", ProjectId = TestApp.Alpha, EnterpriseId = outsider, Mode = "plan", Status = OffboardingStatus.Planning, CorrelationId = "c",
            RequestedBy = TestApp.Paul, RequestedAt = app.Clock.UtcNow, UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
        await app.RunWorker();
        var forged = (await app.Stores.OffboardingRuns.GetAsync(TestApp.Alpha, "off-forged"))!.Value;
        Assert.Equal(OffboardingStatus.Failed, forged.Status);
        Assert.Contains("people inventory", Assert.Single(forged.Leftovers));
        Assert.DoesNotContain(app.Offboarding.Calls, c => c.Contains(outsider, StringComparison.Ordinal));
        Assert.True(app.Directory.User(outsider)!.AccountEnabled);
    }

    [Fact]
    public async Task F5_A_person_another_project_of_the_tenant_still_holds_is_not_offboarded_from_one_project()
    {
        var (app, _) = await Leaver();
        await OwnTenant(app, TestApp.Beta); // two projects name the same (the agent's) tenant
        // The re-check probe: Bob (Requester, BETA) writes a BETA request for ALPHA's person; Bella (PMO, BETA) tries alone.
        var bobs = await app.Submit(TestApp.Intake(enterpriseId: "sample.leaver@company.example", resource: "SAMPLE Test Portal", accessLevel: "User",
            owner: "sample-user-bella@sample.invalid"), TestApp.Bob, TestApp.Beta);
        Assert.True(bobs.Status == 201, bobs.Body);
        Assert.Equal(404, (await Plan(app, user: TestApp.Bella, project: TestApp.Beta)).Status);
        // Even when BETA lists the person too, ALPHA still holds them (Active): neither project offboards alone.
        await AddPerson(app, Upn, TestApp.Beta);
        var shared = await Plan(app, user: TestApp.Bella, project: TestApp.Beta);
        Assert.Equal((409, "person_in_other_project"), (shared.Status, shared.ErrorCode));
        Assert.Equal((409, "person_in_other_project"), (await Plan(app)).Pipe(x => (x.Status, x.ErrorCode)));
        await app.RunWorker();
        Assert.Empty(app.Offboarding.Calls);
        Assert.True(app.Directory.User(Upn)!.AccountEnabled);
        // BETA releases the person (its Data Steward marks them Leaver): ALPHA offboards them.
        var b = (await app.Stores.PeopleInventory.GetAsync(TestApp.Beta, Person.MakeId(Upn)))!;
        await app.Stores.PeopleInventory.ReplaceAsync(b.Value with { Status = PersonStatus.Leaver }, b.ETag);
        var planned = await Planned(app);
        Assert.Equal(202, (await Execute(app, IdOf(planned))).Status);
        await app.RunWorker();
        Assert.Equal("Completed", Status(await Get(app)));
    }

    [Fact]
    public async Task F5_A_person_another_project_granted_access_to_is_released_by_its_people_inventory_never_blocked_for_good()
    {
        // ALPHA granted the person access (a Completed request, no people inventory entry); BETA lists the person and offboards.
        var app = TestApp.With(settings: Settings());
        await OwnTenant(app);
        await OwnTenant(app, TestApp.Beta);
        var completed = await app.To(RequestStatus.Completed);
        var upn = (await app.Request(completed)).DomainId!;
        var listed = (await app.Stores.PeopleInventory.GetAsync(TestApp.Alpha, Person.MakeId(upn)))!; // the SAMPLE joiner: retired, so only the request holds
        listed = await app.Stores.PeopleInventory.ReplaceAsync(listed.Value with { Retired = true }, listed.ETag);
        await AddPerson(app, upn, TestApp.Beta);
        var held = await Plan(app, upn, TestApp.Bella, TestApp.Beta);
        Assert.Equal((409, "person_in_other_project"), (held.Status, held.ErrorCode));
        Assert.Contains("Leaver", held.Json.GetProperty("error").GetProperty("message").GetString());
        // ALPHA's Data Steward lists the person as Leaver: released, although the Completed request stays as it is.
        await app.Stores.PeopleInventory.ReplaceAsync(listed.Value with { Retired = false, Status = PersonStatus.Leaver }, listed.ETag);
        Assert.Equal(RequestStatus.Completed, (await app.Request(completed)).Flow!.Status);
        Assert.Equal(202, (await Plan(app, upn, TestApp.Bella, TestApp.Beta)).Status);
    }

    [Fact]
    public async Task F5_Switched_off_half_way_the_rest_is_left_to_people_and_verified_with_an_escalation()
    {
        var (app, _) = await Leaver();
        var planned = await Planned(app);
        app.Offboarding.FailOnce = $"remove-member {Upn} {GroupA}"; // pauses after the sessions were revoked
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        Assert.Contains(Steps(await Get(app)), s => s.Name == OffboardingJob.Revoke && s.Result == "Done");
        // AGENT_OFFBOARDING switched off (always allowed): the worker runs without the writers.
        var off = new OffboardingJob(app.Stores, app.Directory, app.Offboarding, app.Offboarding, null, null, app.Worker.Notifier, app.Worker.Issues, app.Clock,
            app.Settings.Workflow with { AgentOffboarding = false }, app.Loggers.CreateLogger<OffboardingJob>());
        var calls = app.Offboarding.Calls.Count;
        await off.RunOnceAsync(CancellationToken.None);
        var done = await Get(app);
        Assert.Equal("PartiallyCompleted", Status(done));
        Assert.DoesNotContain(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains("nothing was changed"));
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains("still enabled"));
        Assert.Equal(calls, app.Offboarding.Calls.Count); // no write after the switch
        Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), x => x.Value.Kind == EscalationKind.OffboardingLeftover);
        Assert.Empty(await OffboardingMails(app));
    }

    [Fact]
    public async Task F5_A_confirmed_run_is_finished_and_verified_even_if_the_person_is_retired_meanwhile()
    {
        var (app, _) = await Leaver();
        var planned = await Planned(app);
        app.Offboarding.FailOnce = $"revoke {Upn}"; // Graph unavailable at the first write: the confirmed run waits
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        Assert.Equal("Executing", Status(await Get(app)));
        // HR drops the leaver from the next people export ("Retire missing rows").
        var p = (await app.Stores.PeopleInventory.GetAsync(TestApp.Alpha, Person.MakeId(Upn)))!;
        await app.Stores.PeopleInventory.ReplaceAsync(p.Value with { Retired = true }, p.ETag);
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal("Completed", Status(done));
        Assert.DoesNotContain(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains("nothing was changed"));
        Assert.False(app.Directory.User(Upn)!.AccountEnabled);
        Assert.Empty(app.Directory.GroupsOf(Upn));
        // The project changed this account: a new plan is still allowed (to check or finish it).
        Assert.Equal(202, (await Plan(app)).Status);
    }

    [Fact]
    public async Task F5_A_directory_role_given_after_the_plan_stops_every_entra_and_azure_write()
    {
        var (app, _) = await Leaver();
        var planned = await Planned(app);
        app.Offboarding.Roles[Upn] = ["62e90394-69f5-4237-9190-012177145e10"]; // a directory role, given after the plan
        await Execute(app, IdOf(planned));
        await app.RunWorker();
        var done = await Get(app);
        Assert.Equal("PartiallyCompleted", Status(done));
        Assert.Empty(app.Offboarding.Calls);
        Assert.True(app.Directory.User(Upn)!.AccountEnabled);
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains("directory role"));
        Assert.Empty(await OffboardingMails(app));
    }

    [Fact]
    public async Task F5_A_directory_role_holder_is_left_to_people_and_nothing_is_changed_in_Entra_or_Azure()
    {
        var (app, requestId) = await Leaver();
        app.Offboarding.Roles[Upn] = ["62e90394-69f5-4237-9190-012177145e10"]; // not a group: an Entra directory role
        var planned = await Planned(app);
        Assert.All(Steps(planned).Where(s => s.Name is OffboardingJob.Revoke or OffboardingJob.Tap or OffboardingJob.Rbac or OffboardingJob.Disable
                                             || (s.Name == OffboardingJob.Membership && s.Target != "62e90394-69f5-4237-9190-012177145e10")),
            s => Assert.Equal(("Skipped", true), (s.Result, s.Detail.Contains("directory role", StringComparison.Ordinal))));
        await Execute(app, IdOf(planned));
        await app.RunWorker();

        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        var leftovers = done.Json.GetProperty("leftovers").EnumerateArray().Select(l => l.GetString()!).ToList();
        Assert.Contains(leftovers, l => l.Contains("still enabled"));
        Assert.Contains(leftovers, l => l.Contains("directory role") && l.StartsWith("still in", StringComparison.Ordinal));
        Assert.Contains(leftovers, l => l.StartsWith(OffboardingJob.Revoke, StringComparison.Ordinal));
        Assert.Empty(app.Offboarding.Calls);                                    // no Entra or Azure write at all
        Assert.True(app.Directory.User(Upn)!.AccountEnabled);
        Assert.Contains(GroupA, app.Directory.GroupsOf(Upn));
        Assert.Equal(RequestStatus.Closed, (await app.Request(requestId)).Flow!.Status); // the app stops granting anything more
        Assert.Empty(await OffboardingMails(app));
        Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), x => x.Value.Kind == EscalationKind.OffboardingLeftover);
    }

    [Fact]
    public async Task F5_A_request_opened_after_the_plan_is_a_leftover_and_a_leaver_gets_no_new_request()
    {
        var (app, planRequest) = await Leaver();
        var planned = await Planned(app);
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.Close && s.Target == planRequest);
        var late = await app.SubmitId(TestApp.Intake(enterpriseId: "sample.leaver@company.example", resource: "SAMPLE Sales Lakehouse", accessLevel: "Read"));
        await Execute(app, IdOf(planned));
        await app.RunWorker();

        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.Contains($"request {late} was opened after the plan"));
        Assert.Equal(RequestStatus.Closed, (await app.Request(planRequest)).Flow!.Status);
        Assert.Empty(await OffboardingMails(app));
        // Now a leaver: no new request is taken for the person until the Data Steward marks them Active again.
        var again = await app.Submit(TestApp.Intake(enterpriseId: "sample.leaver@company.example"));
        Assert.Equal((409, "person_is_leaver"), (again.Status, again.ErrorCode));
    }

    [Fact]
    public async Task F5_Double_click_on_plan_and_confirm_runs_everything_once()
    {
        var (app, _) = await Leaver();
        var first = await Plan(app);
        var second = await Plan(app);
        Assert.Equal((202, 200), (first.Status, second.Status));
        Assert.Equal(IdOf(first), IdOf(second));
        Assert.Single(await app.Stores.OffboardingRuns.ListAsync(TestApp.Alpha));
        await app.RunWorker();
        var a = await Execute(app, IdOf(first));
        var b = await Execute(app, IdOf(first));
        Assert.Equal((202, 200), (a.Status, b.Status));
        await app.RunWorker();
        await app.RunWorker();
        Assert.Single(app.Offboarding.Calls, c => c == $"disable {Upn}");
        Assert.Single(app.Offboarding.Calls, c => c == $"revoke {Upn}");
        Assert.Single(await OffboardingMails(app));
        // A later plan is a new run (the first one is finished).
        Assert.NotEqual(IdOf(first), IdOf(await Plan(app)));
    }

    [Fact]
    public async Task F5_Execute_needs_the_switch_a_ready_and_fresh_plan_and_the_right_role()
    {
        var (off, _) = await Leaver(on: false);
        var planned = await Planned(off);
        Assert.Equal("Planned", Status(planned)); // plans are made with the switch off
        var refused = await Execute(off, IdOf(planned));
        Assert.Equal((409, "offboarding_off"), (refused.Status, refused.ErrorCode));
        await off.RunWorker();
        Assert.Empty(off.Offboarding.Calls);

        var (app, _) = await Leaver();
        var r = await Plan(app);
        Assert.Equal((409, "plan_not_ready"), (await Execute(app, IdOf(r))).Pipe(x => (x.Status, x.ErrorCode)));
        await app.RunWorker();
        app.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal((409, "plan_expired"), (await Execute(app, IdOf(r))).Pipe(x => (x.Status, x.ErrorCode)));
        // The expired plan is closed and a new one is made (it never blocks the person's offboarding).
        var again = await Plan(app);
        Assert.Equal(202, again.Status);
        Assert.NotEqual(IdOf(r), IdOf(again));
        var old = (await app.Stores.OffboardingRuns.GetAsync(TestApp.Alpha, IdOf(r)))!.Value;
        Assert.Equal(OffboardingStatus.Failed, old.Status);
        Assert.Contains("plan expired", Assert.Single(old.Leftovers));
        Assert.Contains(await app.Stores.AuditEvents.ListAsync(TestApp.Alpha), a => a.Value.Action == "offboarding-plan-expired");
        await app.RunWorker();
        Assert.Equal(202, (await Execute(app, IdOf(again))).Status);
        await app.RunWorker();
        Assert.Equal("Completed", Status(await Get(app)));

        Assert.Equal(403, (await Plan(app, user: TestApp.Alice)).Status);     // a requester
        Assert.Equal(403, (await Plan(app, user: TestApp.Sam)).Status);       // Security
        Assert.Equal(404, (await Plan(app, user: TestApp.Bob)).Status);       // no role in ALPHA
        Assert.Equal(400, (await app.Call(TestApp.Paul, "POST", $"offboarding/{Uri.EscapeDataString(Upn)}?projectId={TestApp.Alpha}&mode=delete")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Paul, "POST", $"offboarding/not-a-upn?projectId={TestApp.Alpha}&mode=plan")).Status);
        var vestacy = await Plan(app, project: TestApp.Vestacy, user: TestApp.Paul); // another tenant: the agent does not touch it
        Assert.Equal((409, "tenant_not_connected"), (vestacy.Status, vestacy.ErrorCode));
    }

    [Fact]
    public async Task F5_Guest_and_soft_delete_when_switched_on()
    {
        var app = TestApp.With(settings: Settings(delete: true));
        await OwnTenant(app);
        const string guest = "sample.partner_corp.invalid#EXT#@sample.invalid";
        app.Directory.AddUser(guest, guest: true);
        app.Offboarding.Assignments[SubA] = []; // a subscription the agent sees, no role of the guest in it
        await AddPerson(app, guest);
        var planned = await Planned(app, guest);
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.Revoke && s.Result == "Skipped" && s.Detail.Contains("home tenant"));
        Assert.Contains(Steps(planned), s => s.Name == OffboardingJob.Delete && s.Result == "Planned" && s.Detail.Contains("30 days"));
        Assert.Contains(planned.Json.GetProperty("notCovered").EnumerateArray(), n => n.GetString()!.Contains("guest"));
        await Execute(app, IdOf(planned), guest);
        await app.RunWorker();
        Assert.Equal("Completed", Status(await Get(app, guest)));
        Assert.Equal([$"disable {guest}", $"delete-user {guest}"], app.Offboarding.Calls);
        Assert.Null(app.Directory.User(guest));
    }

    /// <summary>
    /// A planned delete that did not happen (refused: User.ReadWrite.All not in the worker's token yet, F61) is a leftover: no
    /// read in verify shows it, so without this the run would be Completed and the PMO told so while the account still exists.
    /// </summary>
    [Fact]
    public async Task F5_A_refused_delete_is_a_leftover_so_no_completion_mail()
    {
        var app = TestApp.With(settings: Settings(delete: true));
        await OwnTenant(app);
        const string upn = "sample.deleted@sample.invalid";
        app.Directory.AddUser(upn);
        app.Offboarding.Assignments[SubA] = [];
        await AddPerson(app, upn);
        app.Offboarding.ForbidAll = "delete-user";
        var planned = await Planned(app, upn);
        await Execute(app, IdOf(planned), upn);
        await app.RunWorker();

        var done = await Get(app, upn);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        Assert.Contains(Steps(done), s => s.Name == OffboardingJob.Delete && s.Result == "Failed" && s.Detail.Contains("F61"));
        Assert.Contains(done.Json.GetProperty("leftovers").EnumerateArray(), l => l.GetString()!.StartsWith(OffboardingJob.Delete, StringComparison.Ordinal));
        Assert.Empty(await OffboardingMails(app));
        Assert.False(app.Directory.User(upn)!.AccountEnabled); // disabled; only the delete is left
    }
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
