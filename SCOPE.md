# SCOPE — PMO onboarding flow (v2, 17 Oct 2026)

v2 replaces the Phase 1 readiness review and the Phase 2/2b/2c Entra hand-off. The **AI agent reviews, validates, summarises, routes, tracks, verifies and reports. It never approves.** People approve; the ID team and the role assignment team make the changes in their own tools and record what they did here.

**v2.1 agent provisioning (Gokul's choice, 17 Oct 2026; off until its permissions are approved):** only after the named Owner and then Security approved, the worker — deterministic code, never the AI model — creates the user, the security group (when the mapping sheet says "to be created") and the membership, and assigns the mapping row's Azure role to the group when the role and scope are on the approved allow-list. The **Temporary Access Pass stays with the ID team.** Guests, privileged rows, ambiguous or missing groups, rows without an Azure role and a write still refused after 24 hours (new permissions can take that long to reach the worker) go to the team, with the reason (and an issue when something failed).
**21 Oct 2026 additions (code; mail needs approval):**
- **Tenant separation:** one project per Entra tenant (e.g. Vestacy, PEST); requests, mapping rows, response records, user roles and notifications never cross projects. The agent reads and writes Entra/Azure only for projects in its own tenant (`AGENT_TENANT_ID`); for another tenant the Entra checks are "not checked by the agent" (risk flag *Tenant not connected to the agent*) and the ID team / role team work in that tenant.
- **Environments:** a project lists its environments (e.g. Dev, PreProd, Prod); the request picks one (dev / ppd / prd understood) and the mapping row must match it. Prod with no group is returned with where the access exists; *Production environment* is a risk flag.
- **Mapping sheet from the workbooks:** group object id, intended job roles, red/amber flag and notes. Red = never sent for approval; amber = caveat shown to the approvers. Each row carries every Azure role of its group (response document: role × resource × scope). The agent assigns a group's roles only when every one is resolved and allow-listed (all or nothing); otherwise the role assignment team assigns and records exactly the listed roles.
- **Notifications by mail** (Azure Communication Services Email, managed identity; off until approved): Owner and Security approval requests, Security alerts, what the agent provisioned (requester, Security), *access ready* with the sign-in name, group and roles to the joiner's **enterprise mail (the Enterprise ID)**, and the completion mail to the requester (22 Oct: see below). Never a TAP. A test environment sends every mail to one tester mailbox.
**22 Oct 2026 additions (Gokul's request; code — the agent TAP needs approval):**
- **Agent TAP:** for an account the agent has just created after both approvals, the worker also creates one **one-time Temporary Access Pass** (lifetime `AGENT_TAP_LIFETIME_MINUTES`, default 8 h, within the tenant's TAP policy) and mails it **once**, with the initial password (force change at first password sign-in) and the MFA set-up steps (Security info → Microsoft Authenticator → delete the pass → set the password), straight to the request's **Enterprise ID mail** (test environment: the tester mailbox). The app keeps a copy with both values masked, plus the times; logs, ADO, prompts and the trail never hold them. No mail sender, guest, existing account, failed pass or failed mail → the ID team issues the pass as before. Permission `UserAuthMethod-TAP.ReadWrite.All` is tenant-wide; the code uses it only for the user it created in the same request. Switch: `deploy_free.sh tap` (dry run → approval).
- **Agent privileged rows** (`AGENT_PRIVILEGED`, off until approved): the agent also provisions rows flagged privileged — e.g. PEST Data Engineer (Storage Blob Data Contributor, Key Vault Secrets Officer, Reader) — after both approvals, with the same checks, the same allow-list and the same RBAC condition. **Never** the privileged administrator roles (Owner, Contributor, User Access Administrator, Role Based Access Control Administrator, Reservations Administrator) or Key Vault Data Access Administrator (F68): a request whose row includes one stays with people, as a member of such a group would hold the role. A task left to a team only because the row was privileged goes back to the agent once this is on, if nobody has picked it up. Switch: `deploy_free.sh privileged` (dry run → approval).
- **Request form:** Tenant, Environment, Resource and Access level are dropdowns filled from the tenant's mapping sheet only (no free text). The Enterprise ID must be the joiner's mail address. A justification template (prefilled with the choices; Copy / Insert) — an unfilled `[ … ]` is refused.
- **Real tenants live, no SAMPLE data:** the live app holds the PEST and Vestacy projects imported from the shared group classification and response document (`import_workbook.py`; real data stays out of git). SAMPLE data remains only for tests and local runs; `seed_sample_data.py --remove-sample` clears it from Cosmos and `deploy_free.sh app` no longer seeds it.
**22 Oct 2026, go-live (Gokul, after the 21 Oct live test reached Completed; code only, no Azure change):**
- **Completion to the requester:** the completion mail goes to the person who submitted the request (with Security, PMO, the ID team and the role assignment team), never the Owner; the Owner is mailed only to decide. The joiner gets *access ready* at the Enterprise ID mail. Provisioning summaries go to the requester and Security.
- **No sign-in step:** step 7 verifies the account, the membership and the exact roles once and completes; it no longer waits for the joiner's first sign-in, a permanent method or the TAP removal.
- **Contact inventory:** Security, ID team, role assignment team, PMO and Operators mail lists (plus escalation managers) per tenant, edited in the app, addresses separated by semicolons; PMO and Operators save; versioned; each change mailed to Security and PMO. Routes mail only, never approval rights.
- **Mail limit (F70):** sent within the ACS limit (Azure-managed domain 10 an hour), most urgent first; over the limit mails wait, never fail; a backlog over an hour is an issue. A verified custom domain (approval) raises the limit.
**30 Oct 2026, v1.1 feature pack — APPROVED for build by Gokul ("approved, use your proposals"; decisions in `docs/V1_1_PLAN.md` section 0).** Code only: every new write and permission stays off until its own dry run and approval, as for v2.1. Instructions: `PMO_Access_Portal_v1.1_Feature_Prompt.md`.
- **F1 Resource\* selector:** options from the signed-in user's tenant mapping sheet only (domains, PH, ACQ, "All Domains + PH + ACQ"), dropdown or checkboxes; resolved access de-duplicated by group object id / normalised name, highest level shown, near-duplicate group names raised as mapping quality issues, unresolved groups never granted (escalation). Proposed: one request per resolved group, linked by a bundle id, each through the full flow.
- **F2 TAP mail to the enterprise mail only:** recipient = the person's EnterpriseEmail from the people inventory, matching the Entra user's mail/UPN; otherwise not sent and an escalation *TAP recipient mismatch*. Never AlternateEmail, a personal domain, a manager, a distribution list or a request-body address; one recipient, no CC/BCC, never redirected; audit only (userId, masked recipient, time, expiry, correlation id).
- **F3 Prod read-only:** in Prod only Read is fulfilled automatically; anything above Read needs an escalation approved by a **ProdApprover** (new role; not the requester), bound to the request revision — proposed as a third approval after Owner and Security, never instead of them.
- **F4 Uploads:** mapping sheet (new I1 columns) and **people inventory** (I2) uploads by a maintainer role: validate → diff → upsert (retire missing rows only when ticked) → version → one-click rollback; an external upload API built but off (`EXTERNAL_UPLOAD_API_ENABLED=false`).
- **F5 Offboarding:** plan → confirm → execute → verify → audit; revoke sessions, delete the TAP, remove memberships and Azure role assignments, disable the account (deleting off). Uses **update/delete/revoke** Graph and ARM calls, the one exception to the *Excluded* table below (Gokul's written approval, 30 Oct 2026): worker only, `GraphOffboardingWriter` / `ArmRoleAssignmentRemover`, behind `AGENT_OFFBOARDING=false` until its permissions are approved. PMO alone is told, and only after a clean verify.
- Not covered by v1.1: Databricks workspace users, mailbox / OneDrive data, licences, projects outside the agent's tenant.

History (v1 + Phase 2 hand-off): `docs/DEPLOYMENT_LOG.md`, `workflows/08-entra-handoff.md` (superseded).

## Flow overview (stated to every requester upfront)
1. Requester submits the request
2. AI review (mapping sheet + response document)
3. Owner approval
4. Security approval
5. ID team creates user/group (+ TAP)
6. Role assignment team assigns roles
7. AI verifies the access, notifies the requester (Completed), audit — no wait for the user's sign-in

## Who does what (strict)
| Who | Does | Never |
|---|---|---|
| AI agent (API + worker) | Reviews, validates, summarises, routes, tracks, verifies, reports. v2.1 (switched on, worker only, after both approvals): creates user, group, membership; assigns allow-listed Azure roles to the group | Approves; writes anything before both approvals; issues or sees a TAP; updates or deletes users, groups or assignments; creates guests; privileged access |
| Owner (named on the request) | Approves the business need | — |
| Security | Approves risk and access level | Approves at the Owner stage of the same request |
| ID team | Issues the TAP; creates the user ID and groups and adds membership when the agent does not (v2.1 off, or left to them) | — |
| Role assignment team | Assigns roles the agent does not (v2.1 off, no Azure role on the row, not allow-listed, privileged) | — |
| PMO | Owns the mapping sheet; informed only | Approves |

Nobody else approves. A decision from anyone else is ignored, logged as an *Unauthorized approval* issue and Security is alerted.

## Sources of truth (read fresh on every request; never hardcoded or cached)
| Source | Holds | Container |
|---|---|---|
| PMO-to-Entra Group Mapping Sheet | resource + access level → Entra group → role (scope, privileged, sensitive, deprecated, group state) | `mappingSheet` |
| Response Document | user, group, role, approvals, justification, assigned roles | `responseDocument` |

A change to the mapping sheet rows of a resource after review re-runs the review of in-flight requests for that resource; if the group or role changed, earlier approvals are superseded.

## Included
| Stage | What the app does |
|---|---|
| 1 Intake | Tenant (project), Enterprise ID (the joiner's enterprise mail address), first/last name, environment (tenant projects), resource and access level (dropdowns from the mapping sheet only), justification (template offered; unfilled placeholders refused), Owner (sign-in name), guest flag. Domain-specific ID derived. Incomplete → rejected listing exactly what is missing. Safe format fixes only (spaces, case). |
| 2 AI review | Checks: intake complete; mapping row exists / single group+role / no conflict / group naming / not deprecated; response document format / no duplicate / no conflict / role match / justification; request vs mapping (level justified, privileged flagged, text clean); Entra cross-check (read-only, or "to be created by ID team"); risk flags (privileged, guest, broad, sensitive). PASS → Owner with AI summary; FAIL → returned to requester + PMO with reasons. Never sent on unreviewed or partly reviewed (Entra check still pending). |
| 3 Approvals | Owner, then Security; valid only from the named Owner / a Security member, in the app (tracked channel tied to the request id and the reviewed run). Reject → remarks captured, requester + PMO notified, stop. Past SLA → reminder, then escalation to the manager. Never skipped, never automatic. |
| 4 ID team | Task with the reviewed user ID and group. The team records what it did and TAP **metadata** only. AI verifies user, group, naming, membership, mapping sheet updated (if the group was to be created) and TAP rules; mismatch → returned with the exact difference. |
| 5 Role team | Task from the response document. The team records exactly what it assigned; AI verifies the exact roles (no extra, none missing) and, where enabled, the Azure role assignment (read-only). |
| 6 Final | Login test (sign-in logs), permanent method registered, TAP deleted, access present; notifications to user/Owner/Security/PMO/teams; ADO work item with the full trail; time per stage and wait time per team. |
| Status tracking | Request ID · Requester · Current stage · Waiting on · Done · Next · Time in stage · ADO link. Statuses: Submitted, AI Review, Returned, Awaiting Owner, Awaiting Security, Rejected, With ID Team, With Role Team, Verifying, Completed, Failed-Needs-Dev. |
| Issue handling | Auto-fix only safe, idempotent items (retry max 3, reuse existing group, fix ID/name format, refresh sheet read). Idempotency key = request id + step. Issues list, end-to-end log analysis, developer issue report, daily summary with wait time per team. |
| Azure OpenAI (coded, off) | Managed identity, structured JSON with schema validation, temperature 0, reviews/recommends only, secrets/TAP masked before the prompt, back-off on 429, token usage logged. |

## TAP rules
After all approvals and group membership. One-time use, shortest lifetime (`TAP_MAX_LIFETIME_MINUTES`, default 60). Never in logs, ADO, mails or prompts (masked `******`). Delivered only to the user by the ID team, separate from the Enterprise ID — except the approved agent TAP (22 Oct 2026): one one-time pass for an account the agent has just created, mailed once to that request's Enterprise ID mail with the initial password and the MFA steps (lifetime up to `AGENT_TAP_LIFETIME_MINUTES`, default 480, as the joiner reads it from mail); the app keeps only a masked copy and the metadata. No blind retries. Not for guest/privileged users without Security approval. The app logs only: issued, timestamp, expiry, one-time flag, issued-by. The API refuses any body that carries a pass value.

## Excluded
| Item | Reason |
|---|---|
| Graph or ARM writes other than v2.1's create calls (user, security group, membership, Azure role assignment, and with the agent TAP the one TAP create for the user it created) — no updates, deletes, licences, access packages, other authentication methods | Least privilege |
| Auto-fixing approvals, role escalation, security settings, deletions | Never automatic |
| Storing or showing a TAP value or a password in the app | TAP rules |
| Self-registration, payment, billing | Out of scope |
| Attachments, OCR, multiple agents | Out of scope |

## UI must never contain
Grant / assign / create-user / payment / self-sign-up controls, password fields or a TAP value field, free-text resource or access level, SAMPLE wording. Decision controls (Approve business need, Approve risk and access level, Reject) only in the decision panel, shown only to the person whose decision is due. Permanent banner: *"The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns: only the named Owner and Security approve; the ID team and the role assignment team make the changes."* — with v2.1 provisioning on: *"… It never approves: only the named Owner and Security approve. Only after both approvals it creates the user, group and membership and assigns the approved role where allowed; the ID team issues the Temporary Access Pass."* (the app reads the mode from `/api/me`). Enforced by `src/web/src/guardrails.test.ts`.
