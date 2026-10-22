using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PmoReview.Core.Workflow;

namespace PmoReview.Adapters.Outbound;

/// <summary>
/// DISABLED in the pilot (NOTIFY_SENDER=Outbox). Sends a notification as mail from one shared mailbox through
/// Microsoft Graph sendMail (application permission Mail.Send, limited to that mailbox with an Exchange Online
/// application access policy). Bodies are already masked and never contain a Temporary Access Pass.
/// </summary>
public sealed class GraphMailSender(HttpClient http, TokenCredential credential, string senderMailbox) : INotificationSender
{
    public string Name => "GraphMail";
    public bool Delivers => true;

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            message = new
            {
                subject = n.Subject,
                body = new { contentType = "Text", content = n.Body },
                toRecipients = n.To.Select(a => new { emailAddress = new { address = a } }).ToArray(),
            },
            saveToSentItems = false,
        });
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(senderMailbox)}/sendMail";
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                res = await http.SendAsync(req, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new OutboundException("MailUnavailable", e);
            }
            catch (Azure.Identity.AuthenticationFailedException e)
            {
                throw new OutboundException("IdentityUnavailable", e);
            }
            using (res)
            {
                if (res.IsSuccessStatusCode) return;
                if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
                    continue;
                }
                throw new OutboundException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "MailForbidden",
                    HttpStatusCode.TooManyRequests => "MailThrottled",
                    >= HttpStatusCode.InternalServerError => "MailUnavailable",
                    _ => "MailRejected",
                });
            }
        }
    }
}
