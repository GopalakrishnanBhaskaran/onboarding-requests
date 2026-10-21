# SCOPE — PMO onboarding flow (v2, 17 Oct 2026)

v2 replaces the Phase 1 readiness review and the Phase 2/2b/2c Entra hand-off. The **AI agent reviews, validates, summarises, routes, tracks, verifies and reports. It never approves.** People approve; the ID team and the role assignment team make the changes in their own tools and record what they did here.

**v2.1 agent provisioning (Gokul's choice, 17 Oct 2026; off until its permissions are approved):** only after the named Owner and then Security approved, the worker — deterministic code, never the AI model — creates the user, the security group (when the mapping sheet says "to be created") and the membership, and assigns the mapping row's Azure role to the group when the role and scope are on the approved allow-list. The **Temporary Access Pass stays with the ID team.** Guests, privileged rows, ambiguous or missing groups, rows without an Azure role and a write still refused after 24 hours (new permissions can take that long to reach the worker) go to the team, with the reason (and an issue when something failed).
**21 Oct 2026 additions (code; mail needs approval):**
- **Tenant separation:** one project per Entra tenant (e.g. Vestacy, PEST); requests, mapping rows, response records, user roles and notifications never cross projects. The agent reads and writes Entra/Azure only for projects in its own tenant (`AGENT_TENANT_ID`); for another tenant the Entra checks are "not checked by the agent" (risk flag *Tenant not connected to the agent*) and the ID team / role team work in that tenant.
- **Environments:** a project lists its environments (e.g. Dev, PreProd, Prod); the request picks one (dev / ppd / prd understood) and the mapping row must match it. Prod with no group is returned with where the access exists; *Production environment* is a risk flag.
- **Mapping sheet from the workbooks:** group object id, intended job roles, red/amber flag and notes. Red = never sent for approval; amber = caveat shown to the approvers. Each row carries every Azure role of its group (response document: role × resource × scope). The agent assigns a group's roles only when every one is resolved and allow-listed (all or nothing); otherwise the role assignment team assigns and records exactly the listed roles.
- **Notifications by mail** (Azure Communication Services Email, managed identity; off until approved): Owner and Security approval requests, Security alerts, what the agent provisioned/assigned (Security, Owner, requester), and *access ready* with the sign-in name, group and roles to the joiner's **enterprise mail (the Enterprise ID)** and the requester. Never a TAP. A test environment sends every mail to one tester mailbox.
History (v1 + Phase 2 hand-off): `docs/DEPLOYMENT_LOG.md`, `workflows/08-entra-handoff.md` (superseded).

## Flow overview (stated to every requester upfront)
1. Requester submits the request
2. AI review (mapping sheet + response document)
3. Owner approval
4. Security approval
5. ID team creates user/group (+ TAP)
6. Role assignment team assigns roles
7. AI verification, notifications, audit

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
| 1 Intake | Enterprise ID (the joiner's enterprise mail, or a user name), first/last name, environment (tenant projects), resource and access level (picked from the mapping sheet), justification, Owner (sign-in name), guest flag. Domain-specific ID derived. Incomplete → rejected listing exactly what is missing. Safe format fixes only (spaces, case). |
| 2 AI review | Checks: intake complete; mapping row exists / single group+role / no conflict / group naming / not deprecated; response document format / no duplicate / no conflict / role match / justification; request vs mapping (level justified, privileged flagged, text clean); Entra cross-check (read-only, or "to be created by ID team"); risk flags (privileged, guest, broad, sensitive). PASS → Owner with AI summary; FAIL → returned to requester + PMO with reasons. Never sent on unreviewed or partly reviewed (Entra check still pending). |
| 3 Approvals | Owner, then Security; valid only from the named Owner / a Security member, in the app (tracked channel tied to the request id and the reviewed run). Reject → remarks captured, requester + PMO notified, stop. Past SLA → reminder, then escalation to the manager. Never skipped, never automatic. |
| 4 ID team | Task with the reviewed user ID and group. The team records what it did and TAP **metadata** only. AI verifies user, group, naming, membership, mapping sheet updated (if the group was to be created) and TAP rules; mismatch → returned with the exact difference. |
| 5 Role team | Task from the response document. The team records exactly what it assigned; AI verifies the exact roles (no extra, none missing) and, where enabled, the Azure role assignment (read-only). |
| 6 Final | Login test (sign-in logs), permanent method registered, TAP deleted, access present; notifications to user/Owner/Security/PMO/teams; ADO work item with the full trail; time per stage and wait time per team. |
| Status tracking | Request ID · Requester · Current stage · Waiting on · Done · Next · Time in stage · ADO link. Statuses: Submitted, AI Review, Returned, Awaiting Owner, Awaiting Security, Rejected, With ID Team, With Role Team, Verifying, Completed, Failed-Needs-Dev. |
| Issue handling | Auto-fix only safe, idempotent items (retry max 3, reuse existing group, fix ID/name format, refresh sheet read). Idempotency key = request id + step. Issues list, end-to-end log analysis, developer issue report, daily summary with wait time per team. |
| Azure OpenAI (coded, off) | Managed identity, structured JSON with schema validation, temperature 0, reviews/recommends only, secrets/TAP masked before the prompt, back-off on 429, token usage logged. |

## TAP rules
After all approvals and group membership. One-time use, shortest lifetime (`TAP_MAX_LIFETIME_MINUTES`, default 60). Never in logs, ADO, mails or prompts (masked `******`). Delivered only to the user by the ID team, separate from the Enterprise ID. No blind retries. Not for guest/privileged users without Security approval. The app logs only: issued, timestamp, expiry, one-time flag, issued-by. The API refuses any body that carries a pass value.

## Excluded
| Item | Reason |
|---|---|
| Graph or ARM writes other than v2.1's four create calls (user, security group, membership, Azure role assignment) — no updates, deletes, licences, access packages, TAP | Least privilege |
| Auto-fixing approvals, role escalation, security settings, deletions | Never automatic |
| Storing or showing a TAP value | TAP rules |
| Self-registration, payment, billing | Out of scope |
| Attachments, OCR, multiple agents | Out of scope |

## UI must never contain
Grant / assign / create-user / payment / self-sign-up controls, password fields or a TAP value field. Decision controls (Approve business need, Approve risk and access level, Reject) only in the decision panel, shown only to the person whose decision is due. Permanent banner: *"The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns: only the named Owner and Security approve; the ID team and the role assignment team make the changes."* — with v2.1 provisioning on: *"… It never approves: only the named Owner and Security approve. Only after both approvals it creates the user, group and membership and assigns the approved role where allowed; the ID team issues the Temporary Access Pass."* (the app reads the mode from `/api/me`). Enforced by `src/web/src/guardrails.test.ts`.
