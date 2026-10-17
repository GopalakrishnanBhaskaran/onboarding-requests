# SCOPE — PMO onboarding flow (v2, 17 Oct 2026)

v2 replaces the Phase 1 readiness review and the Phase 2/2b/2c Entra hand-off. The **AI agent reviews, validates, summarises, routes, tracks, verifies and reports. It never approves, creates or assigns.** People approve; the ID team and the role assignment team make the changes in their own tools and record what they did here.
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
| AI agent (API + worker) | Reviews, validates, summarises, routes, tracks, verifies, reports | Approves, creates users/groups, assigns roles or membership, issues or sees a TAP |
| Owner (named on the request) | Approves the business need | — |
| Security | Approves risk and access level | Approves at the Owner stage of the same request |
| ID team | Creates the user ID and Entra groups, adds membership, issues the TAP | — |
| Role assignment team | Assigns roles | — |
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
| 1 Intake | Enterprise ID, first/last name, resource, access level, justification, Owner (sign-in name), guest flag. Domain-specific ID derived. Incomplete → rejected listing exactly what is missing. Safe format fixes only (spaces, case). |
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
| Graph or ARM **writes** of any kind (users, groups, membership, roles, TAP, access packages) | The agent never creates or assigns |
| Auto-fixing approvals, role escalation, security settings, deletions | Never automatic |
| Storing or showing a TAP value | TAP rules |
| Self-registration, payment, billing | Out of scope |
| Attachments, OCR, multiple agents | Out of scope |

## UI must never contain
Grant / assign / create-user / payment / self-sign-up controls, password fields or a TAP value field. Decision controls (Approve business need, Approve risk and access level, Reject) only in the decision panel, shown only to the person whose decision is due. Permanent banner: *"The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns: only the named Owner and Security approve; the ID team and the role assignment team make the changes."* Enforced by `src/web/src/guardrails.test.ts`.
