using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Outbound;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// Mail (21 Oct 2026): the worker mails the outbox through Azure Communication Services Email (managed identity) when it
/// is switched on; a test environment sends every mail to one tester address, naming the intended recipients; production
/// refuses a redirect.
/// </summary>
public class MailTests
{
    private const string Tester = "tester@example.com";

    private sealed class Capture : INotificationSender
    {
        public List<Notification> Sent { get; } = [];
        public string? FailWith { get; set; }
        public string Name => "Capture";
        public bool Delivers => true;

        public Task SendAsync(Notification n, CancellationToken ct)
        {
            if (FailWith is { } f) throw new OutboundException(f);
            Sent.Add(n);
            return Task.CompletedTask;
        }
    }

    private static async Task<(TestApp App, string Id)> Submitted()
    {
        var app = TestApp.Create();
        var id = await app.SubmitId();
        await app.RunWorker(); // review → Owner approval requested (the test cycle records, it does not send)
        return (app, id);
    }

    private static NotificationDispatcher Dispatcher(TestApp app, INotificationSender sender, string? redirect = null, string env = "test") =>
        new(app.Stores, sender, app.Worker.Issues, app.Clock, NullLogger<NotificationDispatcher>.Instance, redirectTo: redirect, environment: env);

    private static async Task Reset(TestApp app)
    {
        foreach (var v in await app.Stores.Notifications.ListAsync(TestApp.Alpha))
            await app.Stores.Notifications.ReplaceAsync(v.Value with { Status = NotificationStatus.Pending, SentAt = null }, v.ETag);
    }

    [Fact]
    public async Task A_test_environment_sends_every_mail_to_the_tester_naming_the_intended_recipients()
    {
        var (app, id) = await Submitted();
        await Reset(app);
        var sender = new Capture();
        Assert.True(await Dispatcher(app, sender, Tester).RunOnceAsync(CancellationToken.None) > 0);

        var ask = Assert.Single(sender.Sent, n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Equal([Tester], ask.To);
        Assert.StartsWith("[TEST test] Approval needed (Owner)", ask.Subject);
        Assert.Contains("this mail was meant for sample-user-olivia@sample.invalid", ask.Body);
        var stored = (await app.Notifications(id)).Single(n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(["sample-user-olivia@sample.invalid"], stored.To); // real recipients kept
        Assert.Equal([Tester], stored.DeliveredTo);
    }

    [Fact]
    public async Task Without_a_redirect_the_real_recipients_get_the_mail()
    {
        var (app, _) = await Submitted();
        await Reset(app);
        var sender = new Capture();
        await Dispatcher(app, sender, redirect: null, env: "prod").RunOnceAsync(CancellationToken.None);
        var ask = Assert.Single(sender.Sent, n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Equal(["sample-user-olivia@sample.invalid"], ask.To);
        Assert.DoesNotContain("test", ask.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_mail_is_retried_three_times_then_listed_as_an_issue()
    {
        var (app, id) = await Submitted();
        await Reset(app);
        var sender = new Capture { FailWith = "MailForbidden" };
        for (var i = 0; i < 3; i++) await Dispatcher(app, sender, Tester).RunOnceAsync(CancellationToken.None);
        var stored = (await app.Notifications(id)).Single(n => n.Kind == NotificationKind.ApprovalRequested);
        Assert.Equal((NotificationStatus.Failed, "MailForbidden", 3), (stored.Status, stored.LastError, stored.Attempts));
        Assert.Contains(await app.Issues(id), i => i.Kind == IssueKind.NeedsDeveloper && i.Summary.Contains("MailForbidden"));
    }

    [Theory]
    [InlineData("prod", Tester, "test environments only")]
    [InlineData("staging", null, "APP_ENVIRONMENT must be one of")]
    [InlineData("test", "not-a-mail", "one mail address")]
    public void Settings_refuse_a_redirect_in_production_and_unknown_environments(string env, string? redirect, string message)
    {
        var values = new Dictionary<string, string?> { ["APP_ENVIRONMENT"] = env, ["NOTIFY_REDIRECT_TO"] = redirect };
        var e = Assert.Throws<InvalidOperationException>(() => PilotSettings.FromEnvironment(k => values.GetValueOrDefault(k)));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Settings_read_the_environment_the_redirect_the_mail_mode_and_the_agent_tenant()
    {
        var values = new Dictionary<string, string?>
        {
            ["APP_ENVIRONMENT"] = "Test", ["NOTIFY_REDIRECT_TO"] = " Tester@Example.com ", ["NOTIFY_SENDER"] = "AcsEmail",
            ["AGENT_TENANT_ID"] = "11111111-2222-4333-8444-555555555555",
        };
        var w = PilotSettings.FromEnvironment(k => values.GetValueOrDefault(k)).Workflow;
        Assert.Equal(("test", Tester, true, "11111111-2222-4333-8444-555555555555"), (w.AppEnvironment, w.MailRedirectTo, w.MailEnabled, w.AgentTenantId));
        var none = PilotSettings.FromEnvironment(_ => null).Workflow;
        Assert.Equal(("pilot", (string?)null, false, (string?)null), (none.AppEnvironment, none.MailRedirectTo, none.MailEnabled, none.AgentTenantId));
    }

    private sealed class FakeEmailClient : EmailClient
    {
        public List<EmailMessage> Messages { get; } = [];
        public int? FailStatus { get; set; }

        public override Task<EmailSendOperation> SendAsync(WaitUntil wait, EmailMessage message, CancellationToken ct = default)
        {
            if (FailStatus is { } s) throw new RequestFailedException(s, "SAMPLE failure");
            Assert.Equal(WaitUntil.Started, wait);
            Messages.Add(message);
            return Task.FromResult<EmailSendOperation>(null!);
        }
    }

    private static Notification Sample(string body = "Hello\nOpen https://app.example/#/requests/abc?x=1&y=2 <b>") => new()
    {
        Id = "n1", ProjectId = TestApp.Alpha, RequestId = "abc", Kind = NotificationKind.AccessReady, IdempotencyKey = "abc:access-ready",
        To = ["jane.doe@company.example", "sample-user-alice@sample.invalid"], Subject = "Your access is ready", Body = body,
        CreatedAt = DateTimeOffset.UnixEpoch, Status = NotificationStatus.Pending,
    };

    [Fact]
    public async Task The_acs_sender_mails_every_recipient_from_the_donotreply_address_as_text_and_safe_html()
    {
        var client = new FakeEmailClient();
        await new AcsEmailSender(client, "DoNotReply@sample.azurecomm.net").SendAsync(Sample(), CancellationToken.None);
        var m = Assert.Single(client.Messages);
        Assert.Equal("DoNotReply@sample.azurecomm.net", m.SenderAddress);
        Assert.Equal(["jane.doe@company.example", "sample-user-alice@sample.invalid"], m.Recipients.To.Select(a => a.Address));
        Assert.Equal(("Your access is ready", Sample().Body), (m.Content.Subject, m.Content.PlainText));
        Assert.Contains("&lt;b&gt;", m.Content.Html); // escaped, never raw markup from a body
        Assert.Contains("<a href=\"https://app.example/#/requests/abc?x=1&amp;y=2\">", m.Content.Html);
        Assert.True(m.UserEngagementTrackingDisabled);
    }

    [Theory]
    [InlineData(403, "MailForbidden")]
    [InlineData(429, "MailUnavailable")]
    [InlineData(503, "MailUnavailable")]
    [InlineData(400, "MailRejected")]
    public async Task The_acs_sender_reports_a_failure_as_a_category(int status, string category)
    {
        var sender = new AcsEmailSender(new FakeEmailClient { FailStatus = status }, "DoNotReply@sample.azurecomm.net");
        var e = await Assert.ThrowsAsync<OutboundException>(() => sender.SendAsync(Sample(), CancellationToken.None));
        Assert.Equal(category, e.Category);
    }
}
