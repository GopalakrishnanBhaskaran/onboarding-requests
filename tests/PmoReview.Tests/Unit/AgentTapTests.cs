using PmoReview.Adapters;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Agent TAP (Gokul, 22 Oct 2026; off until approved): for an account the agent has just created after both approvals, it
/// issues one one-time Temporary Access Pass and mails it with the initial password and the MFA set-up steps to the
/// Enterprise ID mail — once, directly. The app keeps only a masked copy and the times. Every failure leaves the pass to
/// the ID team, as before.
/// </summary>
public class AgentTapTests
{
    private const string Scope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    private const string EnterpriseMail = "sample.e2001@sample-corp.invalid";
    private const string Upn = "sample.e2001@sample.invalid";
    private const string Group = "SAMPLE-GRP-ALPHA-FIN-VIEW";
    private const string Tester = "sample.tester@sample.invalid";

    private static PilotSettings On(bool tap = true, string? redirect = null) => new()
    {
        Workflow = new WorkflowSettings
        {
            AgentProvisioning = true, AgentRoleAssignment = true, AgentTap = tap, AgentTapLifetimeMinutes = 480, MailEnabled = true,
            RoleAssignAllowedRoleIds = [FakeRoleWriter.Reader], RoleAssignScopes = [Scope], MailRedirectTo = redirect, AppEnvironment = redirect is null ? "pilot" : "test",
        },
    };

    private static async Task<(TestApp App, string Id)> Approved(PilotSettings settings, string enterpriseId = EnterpriseMail, Action<TestApp>? arrange = null)
    {
        var app = TestApp.With(settings: settings);
        arrange?.Invoke(app);
        var v = (await app.Stores.MappingSheet.GetAsync(TestApp.Alpha, "A-FIN-VIEW"))!;
        await app.Stores.MappingSheet.ReplaceAsync(v.Value with
        {
            Role = "Reader", RoleScope = Scope, AzureRoleDefinitionId = FakeRoleWriter.Reader, AzureScope = Scope, Version = v.Value.Version + 1,
        }, v.ETag);
        var id = await app.SubmitId(TestApp.Intake(enterpriseId: enterpriseId, resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
        Assert.Equal(200, (await app.Approve(id, TestApp.Olivia)).Status);
        Assert.Equal(200, (await app.Approve(id, TestApp.Sam)).Status);
        Assert.Empty(app.Mail.Sent); // nothing before the worker, nothing in the API
        return (app, id);
    }

    /// <summary>Everything the app stored, as text: no document may hold the password or the pass.</summary>
    private static async Task<string> Stored(TestApp app)
    {
        var s = app.Stores;
        var all = new List<object>();
        foreach (var p in (await s.Projects.ListAllAsync()).Select(x => x.Value.ProjectId))
        {
            all.AddRange((await s.Requests.ListAsync(p)).Select(x => (object)x.Value));
            all.AddRange((await s.Runs.ListAsync(p)).Select(x => (object)x.Value));
            all.AddRange((await s.Notifications.ListAsync(p)).Select(x => (object)x.Value));
            all.AddRange((await s.Issues.ListAsync(p)).Select(x => (object)x.Value));
            all.AddRange((await s.ResponseDocument.ListAsync(p)).Select(x => (object)x.Value));
            all.AddRange((await s.MappingSheet.ListAsync(p)).Select(x => (object)x.Value));
        }
        return string.Join("\n", all.Select(AppJson.Serialize));
    }

    [Fact]
    public async Task At_the_mail_limit_the_agent_creates_nothing_until_the_sign_in_details_can_go_out_in_the_same_run()
    {
        var (app, id) = await Approved(On());
        // 10 mails already left this hour (Azure-managed domain: 10 an hour, F70).
        for (var i = 0; i < 10; i++)
            await app.Worker.Notifier.RecordSentAsync(TestApp.Alpha, $"other-{i}", "s", NotificationKind.FlowOverview, ["x@contoso.com"], "s", "b", ["x@contoso.com"], CancellationToken.None);

        await app.RunWorker();
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls); // no account without a way to hand over its pass
        Assert.Empty(app.Mail.Sent);
        var flow = (await app.Request(id)).Flow!;
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.Agent), (flow.Status, flow.WaitingOn));
        Assert.Single(flow.Events, e => e.Step == "mail-wait"); // said once, retried every run

        app.Clock.Advance(TimeSpan.FromMinutes(61));
        await app.RunWorker();
        Assert.Contains($"create-user {Upn}", app.Writer.Calls);
        Assert.Single(app.Mail.Sent);
    }

    [Fact]
    public async Task The_agent_mails_the_new_account_its_password_and_one_time_pass_once_and_keeps_neither()
    {
        var (app, id) = await Approved(On());
        await app.RunWorker();

        // Group, account, then at once the pass (the password exists only in memory now), then the membership.
        Assert.Equal([$"create-group {Group}", $"create-user {Upn}", $"create-tap {Upn} 480", $"add-member {Upn} {Group}"], app.Writer.Calls);
        var mail = Assert.Single(app.Mail.Sent);
        var (password, pass) = (Assert.Single(app.Writer.Passwords), Assert.Single(app.Writer.Passes));
        Assert.Equal([EnterpriseMail], mail.To);
        Assert.Equal(NotificationKind.SignInDetails, mail.Kind);
        Assert.Contains($"Sign-in name: {Upn}", mail.Body);
        Assert.Contains($"Temporary password: {password}", mail.Body);
        Assert.Contains($"Temporary Access Pass: {pass}", mail.Body);
        foreach (var step in new[] { "https://aka.ms/mysecurityinfo", "Microsoft Authenticator", "scan the QR code", "https://myapps.microsoft.com", "delete the Temporary Access Pass" })
            Assert.Contains(step, mail.Body);

        // The app keeps a copy with both masked, and nothing it stored holds either value.
        var kept = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.SignInDetails);
        Assert.Equal(NotificationStatus.Sent, kept.Status);
        Assert.Equal([EnterpriseMail], kept.To);
        Assert.Equal([EnterpriseMail], kept.DeliveredTo!);
        Assert.Contains($"Temporary password: {SecretMasker.Mask}", kept.Body);
        Assert.Contains($"Temporary Access Pass: {SecretMasker.Mask}", kept.Body);
        var stored = await Stored(app);
        Assert.DoesNotContain(password, stored);
        Assert.DoesNotContain(pass, stored);

        // The agent wrote the ID step itself (TAP metadata only); the ID team has nothing to do.
        var r = await app.Request(id);
        var task = r.Flow!.IdTeamTask!;
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.Agent, TeamTaskState.Reported, WorkflowAgent.AgentName), (r.Flow.Status, r.Flow.WaitingOn, task.State, task.ReportedBy));
        Assert.True(task.Agent is { UserCreated: true, PasswordMailed: true, TapLeft: null, SignInDetailsSentAt: not null });
        var tap = task.IdReport!.Tap!;
        Assert.Equal((WorkflowAgent.AgentName, true, 480.0), (tap.IssuedBy, tap.OneTime, (tap.ExpiresAt - tap.IssuedAt).TotalMinutes));
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.TeamTask);
        var summary = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.ProvisioningSummary);
        Assert.Contains("sample.idteam.alpha@sample.invalid", summary.To);
        Assert.Contains($"mailed the sign-in details", summary.Body);
        Assert.Contains(EnterpriseMail, summary.Body);

        // Next run: the agent verifies its own ID step (8-hour pass allowed for a mailed pass), assigns the role, verifies the
        // access and completes (the joiner's sign-in is not waited for).
        await app.RunWorker();
        r = await app.Request(id);
        Assert.Equal(RequestStatus.Completed, r.Flow!.Status);
        Assert.Contains(r.Flow.IdTeamTask!.Checks, c => c.Code == VerifyCodes.Tap && c.Status == CheckStatus.Pass);
        Assert.Contains(r.Flow.IdTeamTask.Checks, c => c.Code == VerifyCodes.EntraTap && c.Status == CheckStatus.Pass);
        var ready = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.AccessReady);
        Assert.Contains("sign-in details mailed to you earlier", ready.Body);

        await app.RunWorker();
        Assert.Single(app.Mail.Sent); // once
        Assert.Single(app.Writer.Passes);
    }

    [Fact]
    public async Task In_a_test_environment_the_mail_goes_to_the_test_address_and_names_the_joiner()
    {
        var (app, id) = await Approved(On(redirect: Tester));
        await app.RunWorker();

        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal([Tester], mail.To);
        Assert.StartsWith("[TEST test] Your new account", mail.Subject, StringComparison.Ordinal);
        Assert.Contains($"meant for {EnterpriseMail}", mail.Body);
        var kept = Assert.Single(await app.Notifications(id), n => n.Kind == NotificationKind.SignInDetails);
        Assert.Equal([EnterpriseMail], kept.To);
        Assert.Equal([Tester], kept.DeliveredTo!);
    }

    [Fact]
    public async Task Until_the_tap_permission_reaches_the_agent_nothing_is_created_so_password_and_pass_go_out_together()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Writer.TapPermission = false);
        await app.RunWorker();

        Assert.Empty(app.Writer.Calls);
        Assert.Empty(app.Mail.Sent);
        var r = await app.Request(id);
        Assert.Equal((RequestStatus.WithIdTeam, WaitingOn.Agent), (r.Flow!.Status, r.Flow.WaitingOn));
        Assert.Contains(r.Flow.Events, e => e.Step == "agent-waiting-permission" && e.Note.Contains("TapForbidden", StringComparison.Ordinal));

        app.Writer.TapPermission = true;
        await app.RunWorker();
        Assert.Equal([$"create-group {Group}", $"create-user {Upn}", $"create-tap {Upn} 480", $"add-member {Upn} {Group}"], app.Writer.Calls);
        Assert.Contains($"Temporary password: {app.Writer.Passwords[0]}", Assert.Single(app.Mail.Sent).Body);
    }

    [Fact]
    public async Task If_the_tap_permission_never_arrives_the_agent_provisions_after_a_day_and_the_id_team_issues_the_pass()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Writer.TapPermission = false);
        await app.RunWorker();
        Assert.Empty(app.Writer.Calls);

        app.Clock.Advance(TimeSpan.FromHours(25));
        await app.RunWorker();
        Assert.Equal([$"create-group {Group}", $"create-user {Upn}", $"add-member {Upn} {Group}"], app.Writer.Calls);
        Assert.Empty(app.Mail.Sent);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal((TeamTaskState.Open, true), (task.State, task.Agent!.Completed));
        Assert.Contains("TAP permission had not reached the agent 24 h", task.Instructions);
    }

    [Fact]
    public async Task Without_a_sender_that_mails_the_agent_never_issues_a_pass_and_the_id_team_does_as_before()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Mail.Delivers = false);
        await app.RunWorker();

        Assert.DoesNotContain(app.Writer.Calls, c => c.StartsWith("create-tap", StringComparison.Ordinal));
        var r = await app.Request(id);
        Assert.Equal((WaitingOn.IdTeam, TeamTaskState.Open), (r.Flow!.WaitingOn, r.Flow.IdTeamTask!.State));
        Assert.Contains("ONE-TIME Temporary Access Pass", r.Flow.IdTeamTask.Instructions);
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.SignInDetails);
    }

    [Fact]
    public async Task A_failed_mail_leaves_the_pass_to_the_id_team_and_the_unseen_pass_is_never_kept()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Mail.FailWith = "MailUnavailable");
        await app.RunWorker();

        Assert.Contains($"create-tap {Upn} 480", app.Writer.Calls);
        var r = await app.Request(id);
        var task = r.Flow!.IdTeamTask!;
        Assert.Equal((WaitingOn.IdTeam, TeamTaskState.Open), (r.Flow.WaitingOn, task.State));
        Assert.Contains("MailUnavailable", task.Agent!.TapLeft);
        Assert.Contains("issues a new one", task.Instructions);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper && i.Summary.Contains("Agent TAP left to the ID team", StringComparison.Ordinal));
        Assert.DoesNotContain(await app.Notifications(id), n => n.Kind == NotificationKind.SignInDetails);
        Assert.DoesNotContain(app.Writer.Passes[0], await Stored(app));

        await app.RunWorker();
        Assert.Single(app.Writer.Passes); // not retried by the agent: the ID team issues the next one
    }

    [Fact]
    public async Task A_pass_entra_refuses_is_left_to_the_id_team_with_the_reason()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Writer.TapFailWith = "GraphError");
        await app.RunWorker();

        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal(TeamTaskState.Open, task.State);
        Assert.Contains("Entra did not create the Temporary Access Pass (GraphError)", task.Agent!.TapLeft);
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task A_membership_not_readable_yet_is_retried_after_the_mail_so_the_password_still_goes_out()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Writer.NotReplicatedOnce = true);
        await app.RunWorker();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Contains($"Temporary password: {app.Writer.Passwords[0]}", mail.Body);
        Assert.Equal(WaitingOn.Agent, (await app.Request(id)).Flow!.WaitingOn); // membership retried next run

        await app.RunWorker();
        Assert.Single(app.Mail.Sent);
        Assert.Single(app.Writer.Passes);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal((TeamTaskState.Reported, true), (task.State, task.Agent!.Membership));
    }

    [Fact]
    public async Task A_pass_not_creatable_yet_is_retried_next_run_with_the_mfa_steps_and_no_password()
    {
        // Entra cannot read the brand-new account yet even after the in-run retries: the run is retried; the password
        // from the first run is gone (never kept), so the mail says how to set one after MFA.
        var (app, id) = await Approved(On(), arrange: a => a.Writer.TapFailOnce = "NotReplicated");
        await app.RunWorker();
        Assert.Empty(app.Mail.Sent);
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.DependencyFailure && i.Status == IssueStatus.AutoFixed);

        await app.RunWorker();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.DoesNotContain(app.Writer.Passwords[0], mail.Body);
        Assert.Contains("Temporary password (not included): see step 6", mail.Body);
        Assert.Contains("https://aka.ms/sspr", mail.Body);
        Assert.Contains($"Temporary Access Pass: {app.Writer.Passes[0]}", mail.Body);
        Assert.Single(app.Writer.Users); // the account was created once
        Assert.True((await app.Request(id)).Flow!.IdTeamTask!.Agent is { PasswordMailed: false, SignInDetailsSentAt: not null });
    }

    [Fact]
    public async Task A_short_enterprise_id_is_refused_because_the_sign_in_details_need_a_mail_address()
    {
        var app = TestApp.With(settings: On());
        var r = await app.Submit(TestApp.Intake(enterpriseId: "sample.e2001", resource: "SAMPLE Finance Reports", accessLevel: "Viewer"));
        Assert.Equal(400, r.Status);
        Assert.Contains("enterprise mail address", r.Body);
    }

    [Fact]
    public void The_agent_tap_is_off_unless_switched_on_and_its_lifetime_is_bounded()
    {
        Assert.False(PilotSettings.FromEnvironment(_ => null).Workflow.AgentTap);
        var on = PilotSettings.FromEnvironment(k => k == "AGENT_TAP" ? "true" : null).Workflow;
        Assert.Equal((true, 480), (on.AgentTap, on.AgentTapLifetimeMinutes));
        Assert.Equal(60, PilotSettings.FromEnvironment(k => k == "AGENT_TAP_LIFETIME_MINUTES" ? "5" : null).Workflow.AgentTapLifetimeMinutes);
    }

    [Fact]
    public void A_secret_prints_masked_and_new_passwords_meet_entra_complexity()
    {
        var p = Secret.NewPassword();
        Assert.Equal(SecretMasker.Mask, p.ToString());
        Assert.Equal(SecretMasker.Mask, $"{p}");
        var v = p.Reveal();
        Assert.True(v.Length == 16 && v.Any(char.IsAsciiLetterUpper) && v.Any(char.IsAsciiLetterLower) && v.Any(char.IsAsciiDigit) && v.Any(c => !char.IsAsciiLetterOrDigit(c)));
        Assert.NotEqual(v, Secret.NewPassword().Reveal());
    }
}
