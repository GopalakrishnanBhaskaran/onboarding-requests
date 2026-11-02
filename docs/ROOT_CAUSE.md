# ROOT_CAUSE — the TAP mail did not go only to the user's enterprise ID (F2, 30 Oct 2026)

Source: the code at `2e512fe` (before v1.1). The deployment log records no failed send; the defect is **where** the mail went, not that it failed. Found by reading the code (decision 15); no live mail was sent for this analysis.

## What happened
The agent TAP mail (one-time pass + initial password + MFA steps, `WorkflowAgent.SendSignInDetailsAsync`) was addressed by four rules that, together, could send the pass to someone other than the account's owner:

| # | Cause | Where (at `2e512fe`) | Effect |
|---|---|---|---|
| 1 | **Wrong field.** The recipient was the request's `EnterpriseId`, typed by the requester in the request body, with no check against Entra or any directory of people. | `Messages.cs:18` `Recipients.User(r) => [DomainIds.MailOf(r.EnterpriseId)]`; `WorkflowAgent.Provisioning.cs:298` | Whoever submitted the request chose the mailbox that received the pass (R6, R7: body data decided a security action). |
| 2 | **Different account, different domain.** The account the agent creates is `DomainIds.Derive(EnterpriseId, project suffix)` = `<local part>@<tenant domain>`, but the pass was mailed to the typed address, which can be in any domain — including a personal one. Nothing compared the two. | `RequestService.cs:96` (DomainId), `WorkflowAgent.Provisioning.cs:33` (`TapByAgent` only asked "is it a mail address?") | The pass for `a@<tenant>` went to `a@<other domain>`. In the pilot the typed Enterprise ID was a consumer mailbox, so a pass for a tenant account went to a personal domain. |
| 3 | **Test redirect.** `DirectMail` applied `NotificationDispatcher.Redirect`, so in a test environment (`NOTIFY_REDIRECT_TO`) the pass went to the shared tester mailbox, not to the joiner. | `Outbound.cs:103`; worker `Program.cs:66` | Every TAP in test went to one shared mailbox. |
| 4 | **Guest forms accepted.** Mail addresses go through `MailAddresses.Normalize`, which turns a guest UPN `a_b.com#EXT#@tenant…` into `a@b.com` (built for matching the named Owner). The TAP path had no rule against it. | `MailAddresses.cs:27` (`CallerContext.SignInKey`) | A guest form would have been mailed at the guest's home mailbox. (Agent TAP skipped guests, so this was latent.) |

Also against F2 (not a misdirection): a copy of the TAP mail was stored in `notifications` (pass and password masked, recipient in clear), and the trail named the recipient in clear (`Notifier.RecordSentAsync`, `WorkflowAgent.Provisioning.cs:319`). AlternateEmail did not exist yet, so it was not a cause.

## Fix (v1.1 F2)
- Recipient = `EnterpriseEmail` of the **people inventory** person whose `EnterpriseId` is the created account's UPN, and it must equal that Entra user's `mail` or `userPrincipalName` (read from Entra, F95). Not found, Leaver, empty, guest (`#EXT#` or `userType` Guest), personal domain, manager's address, or any mismatch → **nothing is sent**, an escalation *TAP recipient mismatch* is raised, and the ID team hands over the pass (`TapRecipient`).
- The request body never names the recipient; `Recipients.User` is no longer used for the pass.
- Exactly one `To` recipient, no Cc/Bcc (F96); **never redirected**, in any environment (decision 4).
- Sent with Graph `sendMail` from the one `TAP_MAIL_SENDER` mailbox; `Application Mail.Send` scoped to that mailbox by Exchange RBAC for Applications (F81, decision 19). Without the setting, the agent does not issue a pass.
- Nothing of the mail is stored: no notification copy; the audit event holds only user id, masked recipient, time, expiry and correlation id. Logs carry neither the pass nor the address.

Tests: `tests/PmoReview.Tests/Unit/F2TapRecipientTests.cs` (each F2 edge case by name).

## Change on 2 Nov 2026 (Gokul): the Enterprise ID on the request is the recipient again, as an approved field
Gokul's live test: a joiner who is not in the people inventory got no mail ("TAP recipient mismatch: … not in the people inventory"), so the ID team had to hand the pass over. Decision: the address the requester enters as Enterprise ID is the recipient. Cause 1 above (the requester chose the mailbox) is reduced, not removed, by:
- **Approved field.** The Owner and then Security approve that revision; the approval page and mail name the address as where the pass goes and tell them to reject a mailbox that is not the joiner's own. Changing it means a resubmit, which supersedes both approvals; the agent checks both are current before sending.
- **Refused:** a guest sign-in form, a personal mail domain (cause 2), the requester's, the named Owner's or an approver's own address, a people-inventory person who is not active, an inventory EnterpriseEmail that names another mailbox, the AlternateEmail or the manager's address. Then the F2 inventory rule above applies as before; otherwise the escalation.
- **Unchanged:** one recipient, no Cc/Bcc, never redirected (cause 3), nothing stored, masked in audit and trail (the audit now also says which source named the address).

Residual risk (accepted by Gokul): a requester who enters a corporate-domain mailbox they control, which both approvers miss, receives the joiner's pass. Mitigation available if needed: a people-inventory entry for every joiner (the inventory then overrides a different typed address).

Tests: `F2TapRecipientTests` (rewritten for the new order) and `AgentTapTests`.
