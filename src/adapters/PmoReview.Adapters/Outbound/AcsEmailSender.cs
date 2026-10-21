using System.Net;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
using PmoReview.Core.Workflow;

namespace PmoReview.Adapters.Outbound;

/// <summary>
/// OFF unless NOTIFY_SENDER=AcsEmail (approval-gated: deploy_free.sh email). Sends a notification through Azure
/// Communication Services Email from the Azure-managed DoNotReply address, with the worker's managed identity (no
/// connection string or key; VERIFIED_FACTS F63). Bodies are already masked and never contain a Temporary Access Pass.
/// The send is queued by the service (WaitUntil.Started): delivery to the inbox is tracked by the service, not here.
/// </summary>
public sealed class AcsEmailSender(EmailClient client, string senderAddress) : INotificationSender
{
    public string Name => "AcsEmail";
    public bool Delivers => true;

    public AcsEmailSender(Uri endpoint, TokenCredential credential, string senderAddress)
        : this(new EmailClient(endpoint, credential), senderAddress)
    {
    }

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var message = new EmailMessage(senderAddress, new EmailRecipients(n.To.Select(a => new EmailAddress(a)).ToList()),
            new EmailContent(n.Subject) { PlainText = n.Body, Html = Html(n.Body) })
        {
            UserEngagementTrackingDisabled = true,
        };
        try
        {
            await client.SendAsync(WaitUntil.Started, message, ct);
        }
        catch (RequestFailedException e)
        {
            throw new OutboundException(e.Status switch
            {
                (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => "MailForbidden",
                (int)HttpStatusCode.TooManyRequests or >= 500 or 0 => "MailUnavailable",
                _ => "MailRejected",
            }, e);
        }
        catch (Azure.Identity.AuthenticationFailedException e)
        {
            throw new OutboundException("IdentityUnavailable", e);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new OutboundException("MailUnavailable", e);
        }
    }

    /// <summary>The plain-text body as simple HTML: escaped, line breaks kept, links clickable.</summary>
    public static string Html(string text)
    {
        var escaped = WebUtility.HtmlEncode(text);
        var linked = System.Text.RegularExpressions.Regex.Replace(escaped, @"https://[^\s<]+", m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        return "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;line-height:1.45;white-space:pre-wrap\">" + linked + "</div>";
    }
}
