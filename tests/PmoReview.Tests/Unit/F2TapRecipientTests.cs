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
/// Who receives the TAP mail. Gokul, 2 Nov 2026: the Enterprise ID entered on the request, approved by the Owner and
/// Security, unless refused (guest form, personal domain, the requester's / Owner's / an approver's own address, or the people
/// inventory says otherwise); then v1.1 F2 (docs/ROOT_CAUSE.md): the inventory's EnterpriseEmail checked against the Entra
/// user's mail / UPN. Anything else: nothing is sent, an escalation "TAP recipient mismatch" is raised and the ID team hands
/// the pass over. One recipient, no Cc/Bcc, never redirected, never logged or stored, never another body field.
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
    public async Task An_inventory_entry_without_an_address_leaves_the_approved_Enterprise_ID_as_the_recipient()
    {
        var (app, id) = await Approved(On(), person: false);
        await AddPerson(app, enterpriseEmail: "  ");
        await app.RunWorker();
        Assert.Equal([CorpMail], Assert.Single(app.Mail.Sent).To);
        Assert.Empty(await Escalations(app));
        Assert.Equal(TapRecipient.FromRequestSource, (await app.Request(id)).Flow!.IdTeamTask!.Agent!.SignInDetailsFrom);
    }

    [Fact]
    public async Task A_personal_Enterprise_ID_and_no_inventory_address_send_nothing_and_escalate()
    {
        var (app, id) = await Approved(On(), enterpriseId: "sample.e2001@gmail.com", person: false);
        await AddPerson(app, enterpriseEmail: "  ");
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "the request's Enterprise ID is in a personal mail domain (gmail.com)");
        Assert.Contains("EnterpriseEmail of sample.e2001@sample.invalid is empty", Assert.Single(await Escalations(app)).Reason);

        await app.RunWorker(); // idempotent: one escalation, still no mail
        Assert.Single(await Escalations(app));
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task F2_Guest_EXT_upn_is_never_a_recipient()
    {
        var (app, id) = await Approved(On(), enterpriseId: "sample.e2001@gmail.com", person: false);
        await AddPerson(app, enterpriseEmail: "sample.e2001_sample-corp.invalid#EXT#@sample.invalid");
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "guest sign-in form (#EXT#)");

        // A guest form typed as the Enterprise ID is no mailbox either.
        var r = await app.Request(id);
        Assert.Contains("guest sign-in form", TapRecipient.FromApprovedRequest(r with { EnterpriseId = "sample.e2001_sample-corp.invalid#EXT#@sample.invalid" }, null, Upn).Reason);
        Assert.Contains("guest sign-in form", TapRecipient.FromApprovedRequest(r with { EnterpriseId = "live.com#sample.e2001@sample-corp.invalid" }, null, Upn).Reason);

        // The Entra side as well: a guest account (or a #EXT# UPN) is refused even when the inventory and the request match.
        var person = new Person { Id = "p", ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = Upn, DisplayName = "x", UpdatedBy = "t", UpdatedAt = DateTimeOffset.UnixEpoch };
        var ok = r with { EnterpriseId = Upn };
        Assert.Contains("guest account", TapRecipient.CrossCheck(person, Upn, new DirectoryUser("id", Upn, "Guest", true)).Reason);
        Assert.Contains("guest account", TapRecipient.After(person, Upn, new DirectoryUser("id", Upn, "Guest", true), ok).Reason);
        Assert.False(TapRecipient.CrossCheck(person, Upn, new DirectoryUser("id", "sample.e2001_x.invalid#EXT#@sample.invalid", "Member", true)).Ok);
        Assert.False(TapRecipient.After(person, Upn, new DirectoryUser("id", "sample.e2001_x.invalid#EXT#@sample.invalid", "Member", true), ok).Ok);
        Assert.Contains("Entra returned", TapRecipient.After(person, Upn, new DirectoryUser("id", "someone.else@sample.invalid", "Member", true), ok).Reason);
    }

    [Fact]
    public async Task An_inventory_naming_another_mailbox_than_the_request_sends_nothing_and_escalates()
    {
        // The requester typed one mailbox; the people inventory names another, which Entra does not confirm either.
        var (app, id) = await Approved(On(), enterpriseId: "sample.e2001@sample-other.invalid", person: false);
        await AddPerson(app, enterpriseEmail: CorpMail);
        await app.RunWorker();
        await AssertEscalatedAsync(app, id, "the people inventory names another enterprise mail for sample.e2001@sample.invalid (s***@sample-corp.invalid) than the request (s***@sample-other.invalid)");
        var e = Assert.Single(await Escalations(app));
        Assert.Contains("is neither the Entra mail (none) nor the UPN", e.Reason);
        Assert.DoesNotContain(CorpMail, e.Reason); // masked
        Assert.DoesNotContain("sample.e2001@sample-other.invalid", e.Reason);
    }

    [Fact]
    public async Task The_request_and_the_inventory_agreeing_is_enough_even_without_an_Entra_mail()
    {
        // The usual case: the joiner's corporate mailbox is typed on the request and listed in the inventory; the new
        // account has no mailbox of its own yet. Before 2 Nov this escalated ("neither the Entra mail nor the UPN").
        var (app, id) = await Approved(On());
        await app.RunWorker();
        Assert.Equal([CorpMail], Assert.Single(app.Mail.Sent).To);
        Assert.Empty(await Escalations(app));
        Assert.Equal(TeamTaskState.Reported, (await app.Request(id)).Flow!.IdTeamTask!.State);
    }

    [Fact]
    public async Task The_requester_the_Owner_and_the_approvers_never_receive_the_pass()
    {
        var (app, id) = await Approved(On(), person: false);
        var r = await app.Request(id);
        Assert.True(TapRecipient.FromApprovedRequest(r, null, Upn).Ok);
        var owner = r.Flow!.CurrentApproval(ApprovalStage.Owner)!;
        var security = r.Flow.CurrentApproval(ApprovalStage.Security)!;
        foreach (var address in new[] { r.CreatedByName!, r.Owner!, owner.ByName, security.ByName })
            Assert.Matches("the (requester|named Owner|Owner approver|Security approver)'s own address",
                TapRecipient.FromApprovedRequest(r with { EnterpriseId = address.ToUpperInvariant() }, null, Upn).Reason);
        Assert.Contains("the Security approver's own address", TapRecipient.FromApprovedRequest(r with { EnterpriseId = security.ByName }, null, Upn).Reason);
        Assert.Contains("the requester's own address", TapRecipient.FromApprovedRequest(r with { EnterpriseId = r.CreatedByName }, null, Upn).Reason);
        // Only while both approvals count: a superseded one (the reviewed scope changed) stops it.
        Assert.Contains("not both current", TapRecipient.FromApprovedRequest(
            r with { Flow = r.Flow with { Approvals = [.. r.Flow.Approvals.Select(a => a with { Superseded = true })] } }, null, Upn).Reason);
        Assert.Contains("not a plain mail address", TapRecipient.FromApprovedRequest(r with { EnterpriseId = $"Jane <{CorpMail}>" }, null, Upn).Reason);
        Assert.Contains("no Enterprise ID", TapRecipient.FromApprovedRequest(r with { EnterpriseId = " " }, null, Upn).Reason);
    }

    [Fact]
    public async Task The_approvers_are_told_where_the_sign_in_details_go()
    {
        var (app, id) = await Approved(On(), person: false);
        var asks = (await app.Notifications(id)).Where(n => n.Kind == NotificationKind.ApprovalRequested).ToList();
        Assert.Equal(2, asks.Count); // Owner, then Security
        foreach (var n in asks)
            Assert.Contains($"the AI agent mails the one-time Temporary Access Pass and the initial password to {CorpMail} (the Enterprise ID on this request). Reject the request if", n.Body);
        // Without the agent TAP the line is not there (the ID team hands the pass over).
        var (off, offId) = await Approved(On(tap: false), person: false);
        Assert.DoesNotContain((await off.Notifications(offId)).Where(n => n.Kind == NotificationKind.ApprovalRequested), n => n.Body.Contains("Sign-in details:"));
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
        Assert.Equal([CorpMail], mail.To);
        Assert.DoesNotContain(alternate, mail.Body + mail.Subject);
        Assert.DoesNotContain(alternate, await Stored(app));

        // And when the inventory has nothing else: the alternate address does not stand in.
        var p = new Person { Id = "p", ProjectId = TestApp.Alpha, EnterpriseId = Upn, EnterpriseEmail = null, AlternateEmail = alternate, DisplayName = "x", UpdatedBy = "t", UpdatedAt = DateTimeOffset.UnixEpoch };
        Assert.Null(TapRecipient.FromInventory(p, Upn).To);
        Assert.Contains("equals its AlternateEmail", TapRecipient.FromInventory(p with { EnterpriseEmail = alternate }, Upn).Reason);

        // Typed on the request, the alternate address is refused as well (same local part, so the same account).
        const string typedAlternate = "sample.e2001@sample-personal.invalid";
        var (typed, typedId) = await Approved(On(), enterpriseId: typedAlternate, person: false);
        await AddPerson(typed, enterpriseEmail: null, alternate: typedAlternate);
        await typed.RunWorker();
        await AssertEscalatedAsync(typed, typedId, "the request's Enterprise ID is the AlternateEmail of sample.e2001@sample.invalid");
    }

    [Fact]
    public async Task F2_Body_tries_to_override_recipient()
    {
        // The requester types another domain and adds recipient fields. The extra fields never count; the typed Enterprise ID
        // counts only when the people inventory does not name another mailbox — here it names the account's own UPN, which
        // Entra confirms, so the pass goes there.
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
        var (app, id) = await Approved(On(), intake: intake, person: false);
        await AddPerson(app, enterpriseEmail: Upn);
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
        Assert.Equal("sentTo=s***@sample-corp.invalid; expiresAt=" + app.Clock.UtcNow.AddMinutes(480).ToString("O") + "; error=MailForbidden", audit.Details);
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
    public async Task Not_in_the_inventory_the_approved_Enterprise_ID_gets_it_but_a_leaver_never_does()
    {
        // Gokul, 2 Nov 2026: the joiner is usually not in the inventory yet; the Enterprise ID on the request is the mailbox.
        var (app, id) = await Approved(On(), person: false);
        await app.RunWorker();
        Assert.Equal([CorpMail], Assert.Single(app.Mail.Sent).To);
        Assert.Empty(await Escalations(app));

        var (leaver, leaverId) = await Approved(On(), person: false);
        await AddPerson(leaver, status: PersonStatus.Leaver);
        await leaver.RunWorker();
        await AssertEscalatedAsync(leaver, leaverId, "is not an active person in the people inventory (Leaver)");

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
        var (app, id) = await Approved(On(), enterpriseId: "sample.e2001@sample-other.invalid", person: false);
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
