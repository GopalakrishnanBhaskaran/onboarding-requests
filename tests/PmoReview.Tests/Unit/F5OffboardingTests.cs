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

    private static PilotSettings Settings(bool on = true) => new() { Workflow = new WorkflowSettings { AgentOffboarding = on } };

    /// <summary>A leaver with two direct groups, one nested group, a TAP, one Azure role, an inventory entry and an open request.</summary>
    private static async Task<(TestApp App, string RequestId)> Leaver(bool on = true, bool production = false)
    {
        var app = TestApp.With(settings: Settings(on));
        var user = app.Directory.AddUser(Upn);
        app.Directory.AddMember(GroupA, Upn);
        app.Directory.AddMember(GroupB, Upn);
        app.Offboarding.Nested[Upn] = ["SAMPLE-GRP-ALPHA-LEGACY-USER"]; // through another group: goes with it
        app.Offboarding.Taps[Upn] = [Guid.NewGuid().ToString("D")];
        app.Offboarding.Assign(SubA, user.ObjectId);
        await app.Stores.PeopleInventory.CreateAsync(new Person
        {
            Id = Person.MakeId(Upn), ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = Upn, DisplayName = "SAMPLE Leaver", Domain = "Sales",
            UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
        if (production)
        {
            var p = (await app.Stores.Projects.GetAsync(TestApp.Alpha, TestApp.Alpha))!;
            await app.Stores.Projects.ReplaceAsync(p.Value with { IsProductionTenant = true }, p.ETag);
        }
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
        await app.Stores.PeopleInventory.CreateAsync(new Person
        {
            Id = Person.MakeId(twin), ProjectId = TestApp.Alpha, EnterpriseId = twin, EnterpriseEmail = twin, DisplayName = "SAMPLE Leaver", Domain = "Sales",
            UpdatedBy = "test", UpdatedAt = app.Clock.UtcNow, IsSample = true,
        });
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

        // Outside a production tenant the same person may confirm.
        var (dev, _) = await Leaver();
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
        app.Offboarding.Roles[Upn] = ["62e90394-69f5-4237-9190-012177145e10"];          // not a group (a directory role)
        var forbidden = app.Offboarding.Assign(SubB, app.Directory.User(Upn)!.ObjectId);
        app.Offboarding.DeleteForbidden.Add(forbidden.Id);                              // Azure refuses the delete
        var planned = await Planned(app);
        Assert.Equal(2, Steps(planned).Count(s => s.Name == OffboardingJob.Membership && s.Result == "Skipped"));
        await Execute(app, IdOf(planned));
        await app.RunWorker();

        var done = await Get(app);
        Assert.Equal(("PartiallyCompleted", false), (Status(done), done.Json.GetProperty("pmoNotified").GetBoolean()));
        var leftovers = done.Json.GetProperty("leftovers").EnumerateArray().Select(l => l.GetString()!).ToList();
        Assert.Contains(leftovers, l => l.Contains("SAMPLE-GRP-ALPHA-DRIVE-EDIT") && l.Contains("dynamic"));
        Assert.Contains(leftovers, l => l.Contains("directory role"));
        Assert.Contains(leftovers, l => l.Contains("Reader") && l.Contains("still assigned"));
        Assert.Contains(Steps(done), s => s.Name == OffboardingJob.Rbac && s.Result == "Failed" && s.Detail.Contains("ArmForbidden"));
        Assert.Empty(await OffboardingMails(app));
        var e = Assert.Single(await app.Stores.Escalations.ListAsync(TestApp.Alpha), x => x.Value.Kind == EscalationKind.OffboardingLeftover).Value;
        Assert.Contains("No completion mail was sent", e.Reason);
        Assert.False(app.Directory.User(Upn)!.AccountEnabled); // everything that could be done was done
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
        var app = TestApp.With(settings: new PilotSettings { Workflow = new WorkflowSettings { AgentOffboarding = true, OffboardDeleteUser = true } });
        const string guest = "sample.partner_corp.invalid#EXT#@sample.invalid";
        app.Directory.AddUser(guest, guest: true);
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
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
