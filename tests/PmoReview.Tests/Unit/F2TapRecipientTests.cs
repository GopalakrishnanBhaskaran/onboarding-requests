using System.Net;
using System.Text.Json;
using PmoReview.Adapters.Outbound;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Contract;
using PmoReview.Tests.Support;
using Xunit;
using static PmoReview.Tests.Unit.AgentTapTests;

namespace PmoReview.Tests.Unit;

/// <summary>
/// v1.1 F2 (bug fix, docs/ROOT_CAUSE.md): the TAP mail goes only to the people inventory's EnterpriseEmail of the account,
/// checked against the Entra user's mail / UPN. Anything else: nothing is sent, an escalation "TAP recipient mismatch" is
/// raised and the ID team hands the pass over. One recipient, no Cc/Bcc, never redirected, never logged or stored.
/// Each edge case of the feature pack (section B.8, F2) is one test here.
/// </summary>
public class F2TapRecipientTests
{
    private const string Upn = "sample.e2001@sample.invalid";
    private const string CorpMail = "sample.e2001@sample-corp.invalid";

    private static async Task<IReadOnlyList<Escalation>> Escalations(TestApp app) =>
        (await app.Stores.Escalations.ListAsync(TestApp.Alpha)).Select(e => e.Value).Where(e => e.Kind == EscalationKind.TapRecipientMismatch).ToList();

    /// <summary>Nothing was mailed, no pass was created, the account still exists and the ID team hands the pass over.</summary>
    private static async Task AssertEscalatedAsync(TestApp app, string id, string reason)
    {
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(app.Writer.Passes);
        Assert.Contains($"create-user {Upn}", app.Writer.Calls);
        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal((TeamTaskState.Open, true), (task.State, task.Agent!.Completed));
        Assert.Contains("TAP recipient mismatch", task.Agent.TapLeft);
        Assert.Contains(reason, task.Agent.TapLeft);
        Assert.Contains("ONE-TIME Temporary Access Pass", task.Instructions); // the ID team issues it
        var e = Assert.Single(await Escalations(app));
        Assert.Equal((EscalationStatus.Open, id, Upn), (e.Status, e.RequestId, e.User));
        Assert.Contains(reason, e.Reason);
        Assert.Contains((await app.Stores.AuditEvents.ListAsync(TestApp.Alpha)).Select(a => a.Value), a => a.Action == "escalation-raised" && a.Details!.Contains(e.Id));
        Assert.DoesNotContain((await app.Stores.AuditEvents.ListAsync(TestApp.Alpha)).Select(a => a.Value), a => a.Action == "tap-mail-sent");
    }

    [Fact]
    public async Task F2_EnterpriseEmail_empty_sends_nothing_and_escalates()
    {
        var (app, id) = await Approved(On(), person: false);
        await AddPerson(app, enterpriseEmail: "  ");
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "EnterpriseEmail of sample.e2001@sample.invalid is empty");

        await app.RunWorker(); // idempotent: one escalation, still no mail
        Assert.Single(await Escalations(app));
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task F2_Guest_EXT_upn_is_never_a_recipient()
    {
        var (app, id) = await Approved(On(), person: false);
        await AddPerson(app, enterpriseEmail: "sample.e2001_sample-corp.invalid#EXT#@sample.invalid");
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "guest sign-in form (#EXT#)");

        // The Entra side as well: a guest account (or a #EXT# UPN) is refused even when the inventory matches.
        var person = new Person { Id = "p", ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = Upn, DisplayName = "x", UpdatedBy = "t", UpdatedAt = DateTimeOffset.UnixEpoch };
        Assert.Contains("guest account", TapRecipient.CrossCheck(person, Upn, new DirectoryUser("id", Upn, "Guest", true)).Reason);
        Assert.False(TapRecipient.CrossCheck(person, Upn, new DirectoryUser("id", "sample.e2001_x.invalid#EXT#@sample.invalid", "Member", true)).Ok);
    }

    [Fact]
    public async Task F2_Inventory_vs_Entra_mismatch_sends_nothing_and_escalates()
    {
        // The inventory names the corporate mailbox, but Entra has no such mail on the account and its UPN differs.
        var (app, id) = await Approved(On(), person: false);
        await AddPerson(app, enterpriseEmail: CorpMail);
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "is neither the Entra mail (none) nor the UPN");
        var e = Assert.Single(await Escalations(app));
        Assert.DoesNotContain(CorpMail, e.Reason); // masked
        Assert.Contains("s***@sample-corp.invalid", e.Reason);
    }

    [Fact]
    public async Task F2_Entra_mail_that_matches_the_inventory_is_the_one_recipient()
    {
        // The other shape of the same check: the account's Entra mail is the corporate mailbox the inventory names.
        var (app, id) = await Approved(On(), person: false, arrange: a => a.Writer.MailOf[Upn] = CorpMail);
        await AddPerson(app, enterpriseEmail: CorpMail.ToUpperInvariant());
        await app.RunWorker();
        Assert.Equal([CorpMail], Assert.Single(app.Mail.Sent).To);
        Assert.Empty(await Escalations(app));
        Assert.Equal("s***@sample-corp.invalid", (await app.Request(id)).Flow!.IdTeamTask!.Agent!.SignInDetailsSentTo);
    }

    [Fact]
    public async Task F2_AlternateEmail_present_is_never_used()
    {
        const string alternate = "sample.e2001.private@sample-personal.invalid";
        var (app, id) = await Approved(On(), person: false);
        await AddPerson(app, alternate: alternate);
        await app.RunWorker();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal([Upn], mail.To);
        Assert.DoesNotContain(alternate, mail.Body + mail.Subject);
        Assert.DoesNotContain(alternate, await Stored(app));

        // And when the inventory has nothing else: the alternate address does not stand in.
        var p = new Person { Id = "p", ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = null, AlternateEmail = alternate, DisplayName = "x", UpdatedBy = "t", UpdatedAt = DateTimeOffset.UnixEpoch };
        Assert.Null(TapRecipient.FromInventory(p, Upn).To);
        Assert.Contains("equals its AlternateEmail", TapRecipient.FromInventory(p with { EnterpriseEmail = alternate }, Upn).Reason);
    }

    [Fact]
    public async Task F2_Body_tries_to_override_recipient()
    {
        // The requester types another domain and adds recipient fields: the body never decides where the pass goes.
        const string attacker = "sample.attacker@sample-evil.invalid";
        var intake = new
        {
            enterpriseId = "sample.e2001@sample-evil.invalid", firstName = "Sample", lastName = "Joiner", resource = "SAMPLE Finance Reports", accessLevel = "Viewer",
            // (Instructions in the text are a review finding and return the request: covered by the injection tests.)
            justification = "SAMPLE: joins the finance team to read the monthly reports for the controlling review.",
            owner = "sample-user-olivia@sample.invalid", guest = false,
            recipient = attacker, to = new[] { attacker }, cc = new[] { attacker }, bcc = new[] { attacker }, notifyTo = attacker, tapRecipient = attacker,
            alternateEmail = attacker, enterpriseEmail = attacker,
        };
        var (app, id) = await Approved(On(), intake: intake);
        Assert.Equal(Upn, (await app.Request(id)).DomainId); // the account in the tenant's domain

        await app.RunWorker();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal([Upn], mail.To);
        Assert.DoesNotContain("sample-evil.invalid", mail.Body + string.Join(";", mail.To));
    }

    [Fact]
    public async Task F2_Mail_send_fails_leaves_the_pass_to_the_id_team_and_audits_without_the_pass()
    {
        var (app, id) = await Approved(On(), arrange: a => a.Mail.FailWith = "MailForbidden");
        await app.RunWorker();

        var task = (await app.Request(id)).Flow!.IdTeamTask!;
        Assert.Equal(TeamTaskState.Open, task.State);
        Assert.Contains("could not be sent (MailForbidden)", task.Agent!.TapLeft);
        var audit = Assert.Single((await app.Stores.AuditEvents.ListAsync(TestApp.Alpha)).Select(a => a.Value), a => a.Action == "tap-mail-failed");
        Assert.Equal("sentTo=s***@sample.invalid; expiresAt=" + app.Clock.UtcNow.AddMinutes(480).ToString("O") + "; error=MailForbidden", audit.Details);
        Assert.DoesNotContain(app.Writer.Passes[0], await Stored(app));
        Assert.Empty(await Escalations(app)); // the recipient was right; the ID team re-issues the pass
    }

    [Fact]
    public async Task F2_TAP_never_appears_in_logs()
    {
        var logs = new CapturingLoggerFactory();
        var (app, _) = await Approved(On(), loggers: logs);
        await app.RunWorker();
        await app.RunWorker();
        var (failing, _) = await Approved(On(), loggers: logs, arrange: a => a.Mail.FailWith = "MailUnavailable");
        await failing.RunWorker();

        Assert.Single(app.Mail.Sent);
        Assert.Single(failing.Writer.Passes);
        foreach (var secret in app.Writer.Passes.Concat(app.Writer.Passwords).Concat(failing.Writer.Passes).Concat(failing.Writer.Passwords))
            Assert.DoesNotContain(secret, logs.Text);
        Assert.DoesNotContain(Upn, logs.Lines.Where(l => l.Contains("sign-in", StringComparison.OrdinalIgnoreCase))); // the recipient is not logged either
        Assert.Contains(logs.Lines, l => l.Contains("sign-in details not sent MailUnavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F2_Not_in_the_inventory_or_a_leaver_is_never_mailed()
    {
        var (app, id) = await Approved(On(), person: false);
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "is not in the people inventory");

        var p = new Person { Id = "p", ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = Upn, DisplayName = "x", UpdatedBy = "t", UpdatedAt = DateTimeOffset.UnixEpoch };
        Assert.Contains("not an active person", TapRecipient.FromInventory(p with { Status = PersonStatus.Leaver }, Upn).Reason);
        Assert.Contains("retired", TapRecipient.FromInventory(p with { Retired = true }, Upn).Reason);
        Assert.Contains("names sample.other@sample.invalid", TapRecipient.FromInventory(p with { EnterpriseId = "sample.other@sample.invalid" }, Upn).Reason);
    }

    [Fact]
    public void F2_Personal_domains_managers_and_display_names_are_refused()
    {
        var p = new Person { Id = "p", ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = Upn, DisplayName = "x", UpdatedBy = "t", UpdatedAt = DateTimeOffset.UnixEpoch };
        Assert.Equal(Upn, TapRecipient.FromInventory(p, Upn).To);
        Assert.Contains("personal mail domain (gmail.com)", TapRecipient.FromInventory(p with { EnterpriseEmail = "someone@gmail.com" }, Upn).Reason);
        Assert.Contains("personal mail domain", TapRecipient.FromInventory(p with { EnterpriseEmail = "someone@Outlook.com" }, Upn).Reason);
        Assert.Contains("manager's address", TapRecipient.FromInventory(p with { ManagerEnterpriseId = Upn.ToUpperInvariant() }, Upn).Reason);
        Assert.Contains("not a plain mail address", TapRecipient.FromInventory(p with { EnterpriseEmail = $"Jane <{Upn}>" }, Upn).Reason);
        Assert.Contains("not a plain mail address", TapRecipient.FromInventory(p with { EnterpriseEmail = $"mailto:{Upn}" }, Upn).Reason);
        Assert.Contains("not a plain mail address", TapRecipient.FromInventory(p with { EnterpriseEmail = $"{Upn}; boss@sample.invalid" }, Upn).Reason);
        Assert.Equal("s***@sample.invalid", TapRecipient.Masked(Upn));
    }

    [Fact]
    public async Task F2_One_recipient_no_cc_or_bcc_and_no_redirect()
    {
        // DirectMail refuses anything but one plain address.
        var mail = new FakeDirectMail();
        Notification N(params string[] to) => new()
        {
            Id = "n", ProjectId = TestApp.Alpha, Kind = NotificationKind.SignInDetails, IdempotencyKey = "k", To = to, Subject = "S", Body = "B",
            CreatedAt = DateTimeOffset.UnixEpoch, Status = NotificationStatus.Pending,
        };
        Assert.Equal("NotOneRecipient", (await Assert.ThrowsAsync<OutboundException>(() => mail.SendNowAsync(N(Upn, CorpMail), CancellationToken.None))).Category);
        Assert.Equal("NotOneRecipient", (await Assert.ThrowsAsync<OutboundException>(() => mail.SendNowAsync(N(), CancellationToken.None))).Category);
        Assert.Equal("NotOneRecipient", (await Assert.ThrowsAsync<OutboundException>(() => mail.SendNowAsync(N($"Jane <{Upn}>"), CancellationToken.None))).Category);
        Assert.Empty(mail.Sent);

        // Graph sendMail from the TAP sender mailbox: toRecipients only, one address, unchanged subject.
        var h = new RouteHandler().On(HttpMethod.Post, "https://graph.microsoft.com/v1.0/users/sample.tap-sender@sample.invalid/sendMail", "", HttpStatusCode.Accepted);
        var sent = await new DirectMail(new GraphMailSender(new HttpClient(h), new StaticCredential(), "sample.tap-sender@sample.invalid")).SendNowAsync(N(Upn), CancellationToken.None);
        Assert.Equal([Upn], sent);
        var message = JsonDocument.Parse(h.Calls.Single().Body).RootElement.GetProperty("message");
        Assert.Equal([Upn], message.GetProperty("toRecipients").EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString()));
        Assert.False(message.TryGetProperty("ccRecipients", out _));
        Assert.False(message.TryGetProperty("bccRecipients", out _));
        Assert.Equal("S", message.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task F2_The_escalation_is_listed_for_the_id_team_and_pmo_without_the_address()
    {
        var (app, id) = await Approved(On(), person: false);
        await AddPerson(app, enterpriseEmail: CorpMail);
        await app.RunWorker();
        var list = await app.Call(TestApp.Ivan, "GET", $"projects/{TestApp.Alpha}/escalations");
        Assert.Equal(200, list.Status);
        Assert.Contains("TapRecipientMismatch", list.Body);
        Assert.DoesNotContain(CorpMail, list.Body);
        Assert.Equal(200, (await app.Call(TestApp.Paul, "GET", $"projects/{TestApp.Alpha}/escalations")).Status);
        Assert.Equal(403, (await app.Call(TestApp.Alice, "GET", $"projects/{TestApp.Alpha}/escalations")).Status);
        Assert.Equal(404, (await app.Call(TestApp.Bob, "GET", $"projects/{TestApp.Alpha}/escalations")).Status);
        // Only ProdWrite escalations are decided through the ProdApprover endpoint.
        var e = Assert.Single(await Escalations(app));
        Assert.NotEqual(200, (await app.Call("sample-user-petra", "POST", $"projects/{TestApp.Alpha}/escalations/{e.Id}/decision",
            new { decision = "Approved", remarks = "SAMPLE", requestRevision = 1 })).Status);
        Assert.Equal(EscalationStatus.Open, Assert.Single(await Escalations(app)).Status);
        _ = id;
    }
}
