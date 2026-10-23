# Go-live — Friday 23 Oct 2026

Release: branch `claude/eager-ride-lx6he8` (builds on `13ca4b9`, the 22 Oct handoff). Code only: **no Azure resource or permission change, so no approval is needed** for any step below. The live end-to-end test reached Completed on 21–22 Oct; this release fixes what it showed about notifications.

## What changes for people
| Before | Now |
|---|---|
| The completion mail went to the Owner and Security | It goes to the **requester** (who asked for the account), with Security, PMO, the ID team and the role assignment team, in one mail. The Owner is mailed only to approve. |
| Step 7 waited for the joiner's first sign-in, Authenticator and TAP removal | Step 7 checks the account, the membership and the roles once and completes, in the same agent run. A request still waiting for a sign-in completes on the next run. |
| Team addresses came from the import (all the test mailbox) | **Contact inventory** page: Security, ID team, role assignment team, PMO, Operators, addresses separated by semicolons, plus escalation managers. PMO and Operators edit it. |
| Over 10 mails an hour the mail service refused mail and it failed after 3 tries | Mail waits in the queue and goes out in order, approvals and team tasks first. Nothing is lost. |

Edge cases covered: guest sign-in names (`…#EXT#@…`, `live.com#…`) mailed at the real mailbox; a bad address is skipped and the rest still get the mail; an approval or team task with no address raises a High issue; more than 50 recipients go out in batches; a re-import no longer overwrites contacts saved in the app; the agent creates an account only when it can also mail its sign-in details in the same run.

## Steps (Azure Cloud Shell, Bash)
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; az account set --subscription $SUB
cd ~ && rm -rf onboarding-requests && git clone -q -b claude/eager-ride-lx6he8 https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests
git log --oneline -1
tools/deploy_free.sh params $SUB
tools/deploy_free.sh worker $SUB      # code only
tools/deploy_free.sh app $SUB         # code only; ends "Deployed commit … to https://icy-smoke-…"
```
1. **Fill the Contact inventory** (PMO, in the app). For **each tenant** (switch tenant at the top right): menu **Contact inventory** → type each team's addresses separated by `;` → **Save contacts**.
   - Security, ID team and role assignment team need at least one address each; a mail-enabled group counts as one.
   - The page names every wrong entry; nothing is saved until all are mail addresses.
   - Security and PMO get a "Contact inventory changed" mail for each save.
2. **Smoke test, still in test mode** (every mail goes to the Gmail test mailbox, naming who it was meant for):
   - submit one PEST Dev Support Reader request, approve as Owner and Security;
   - the "Completed" mail must say *meant for* the requester first, then the teams, and **not** the Owner;
   - no step waits for the joiner's sign-in.
3. **Switch mail to the real people:**
   ```bash
   tools/deploy_free.sh environment $SUB prod    # settings only: APP_ENVIRONMENT=prod, no redirect, on worker and website
   ```
   The website header turns **PROD** and the yellow "test environment" bar disappears.
4. **Check:**
   ```bash
   bash tools/verify_features.sh $SUB | tee .local/verify.txt
   ```
   Lines **6c** (contact inventory filled), **14** (no failed or stuck mail) and **15** (prod, no redirect) must be PASS. Line 12 stays PASS from the test.
5. **Send the go-live notification** below.

## Watch on day 1
- **Mail limit.** The Azure-managed mail domain sends at most **10 mails an hour** (5 a minute), and one request takes about 7. A burst of requests delays mail, it does not lose it. *Issues and reports* shows "mail(s) waiting for over an hour" when that happens. Fix for volume: a verified custom mail domain (Azure + DNS change, needs approval), then `MAIL_MAX_PER_MINUTE=30`, `MAIL_MAX_PER_HOUR=100` on the worker.
- **No address.** A High issue "… mail reached nobody" means an empty team list: fill it on the Contact inventory page.
- **Wrong address.** The request's *Notifications* tab shows "not mailed (not a mail address): …". Fix it on the Contact inventory page.

## Rollback
- Mail back to the test mailbox at once: `tools/deploy_free.sh environment $SUB test --redirect gopalakrishnan.baskaran@gmail.com`.
- Previous code: `git checkout 13ca4b9 && tools/deploy_free.sh worker $SUB && tools/deploy_free.sh app $SUB`. Everything this release stores stays readable by the previous code (new fields are ignored; the contact-change mail uses an existing kind). The previous code still mails the saved team lists but has no page to edit them, and it waits for the sign-in again.

## Still open (not needed for go-live; each Azure change needs approval)
- Real PEST / Vestacy tenants: the agent writes only in its own tenant. For a project bound to another tenant (`--tenant-id <that tenant>`), the teams do the work there and the app tracks, approves, routes and mails. Moving PEST from the pilot test scope to its real scopes is a re-import (`--resolve` / `--scope-map`), and it overwrites row edits made in the app.
- `AuditLog.Read.All` on the worker is no longer used (no sign-in check). Removing it is optional and a permission change.

## Go-live notification (ready to send)
> **Subject:** Onboarding requests app is live from 23 Oct
>
> Hi all,
>
> The onboarding requests app is live from today.
>
> - Requesters: submit new ID and access requests in the app. You get a mail when the request is received and when it is complete.
> - Owners: you get a mail only when your approval is needed.
> - Security, ID team and role assignment team: tasks and updates come to the addresses on the app's Contact inventory page. PMO can change them there.
> - A request completes once the access is verified. It no longer waits for the new user's first sign-in.
>
> Link: https://icy-smoke-04dcd4300.6.azurestaticapps.net
>
> Questions or issues: reply to this mail.
>
> Thanks,
> Gokul
