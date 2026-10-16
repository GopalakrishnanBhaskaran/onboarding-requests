# SCOPE — PMO Request Review, Phase 1 Pilot

**Status:** Draft. The "Final Power Apps Build Prompts (LOCKED v1.0, 9 Oct 2026)" pack was **not attached** to the build session. This table is built from the pilot prompt (v1.0, 16 Oct 2026) only. It must be reconciled line by line against the locked pack before plan approval (Q7).

The pilot checks whether an onboarding/access request is ready for human review. It **does not approve, route, fulfil or grant anything.**

## Phase 2 addition — Entra hand-off (approved by Gokul, 16 Oct 2026)
Option A: after a Reviewer accepts a ready review, the app gives the new joiner the **My Access link** of the mapped **Entra access package**. Entra runs the approval, adds the group membership, enforces the end date and keeps the audit trail. A background worker reads Entra (read-only) and shows the status in the app. The app still never approves, assigns or removes access. Paid from Azure credits, budget ₹2,000/month. Workflow: `workflows/08-entra-handoff.md`.

| Now included (Phase 2) | Still excluded |
|---|---|
| New joiner sign-in name (UPN) on the request, format-checked, never looked up | Creating users or groups from the app |
| Mapping → access package link (Maintainer, versioned) | Graph writes; `adminAdd` direct assignment |
| Hand-off record + My Access link on Accepted | Approval screens in the app |
| Read-only status sync (requested, pending approval, delivered, denied, expired, end-date check) | Emails/Teams from the app (Entra sends its own) |

### Phase 2b — automatic Entra assignment (approved by Gokul, 16 Oct 2026, in this session)
Choices: **Reviewer Accept = access decision**, **four-eyes on**, **admin script for the Entra setup**. Off by default (`enableAutoAssign` / `ENTRA_AUTO_ASSIGN`).
When on, a **second Reviewer** (not the person who created or analysed the request) accepting a *Ready for human review* run is the access decision. The worker then files **one** Entra `adminAdd` assignment request for the snapshot joiner, on the pinned mapping's access package, through the **"PMO Review auto-assign"** policy (direct assignments only, no self-requests, no approval stage, max 180 days), for the reviewed start/end dates. Entra adds the group membership and removes it on the end date; the worker reads the status back.

| Now included (Phase 2b) | Still excluded |
|---|---|
| One Graph write: `POST assignmentRequests` with `requestType=adminAdd`, only for packages in the one allowed catalog | Any other Graph write (no removals, no updates, no group/user/policy changes) from the app or worker |
| Graph `User.ReadBasic.All` to resolve the joiner's object id | Creating users; looking up anything else about the joiner |
| Admin setup script (catalog, package, policy, mapping link) run by a Global Administrator | The worker creating or changing access packages or policies |

## Included
| Area | What the pilot does |
|---|---|
| Request capture | PMO user creates/edits a request for their own project with minimum fields |
| Snapshot | Server freezes the request at Analyze time with a SHA-256 hash |
| Deterministic checks | Mandatory fields, date/type consistency, supported project/environment, published + effective requirement exists |
| Reference lookups | Two read-only tools: published requirements, approved role mapping (project-scoped, version-pinned) |
| Review result | Findings with citations, mapping assessment, clarification questions, readiness |
| Readiness | NeedsInformation / ReadyForHumanReview / ManualReviewRequired. Host computes it, not the reviewer |
| Run history | Every run kept, with revision, versions used, Superseded label |
| Reviewer feedback | Accepted / Corrected / Escalated on review **quality** (not access approval) |
| Maintainer | Publish SAMPLE rules and role mappings (server-side role check) |
| Operator | Mark stuck runs as Failed (timeout) |
| Paid adapters | Azure OpenAI reviewer and queue dispatcher coded, unit-tested with mocks, **disabled** |

## Deferred / excluded
| Item | Reason |
|---|---|
| **Fulfilment of any kind** | Pilot ends at human review |
| Approval routing / approver workflow | In Entra (Phase 2), never in this app |
| Identity creation, Microsoft Graph writes | Out of scope (Graph **reads** by the worker are Phase 2) |
| Group assignment, credential issue | Entra assigns via the access package (Phase 2); never this app |
| Email / Teams notifications | Out of scope |
| Ticket creation or sync | Out of scope |
| Billing / chargeback | Out of scope |
| Multiple agents / agent framework | Single deterministic reviewer |
| Attachments, OCR | Out of scope |
| AI model at runtime | Coded but disabled (cost) |
| Background queue processing | Coded as stub; inline in pilot |
| App Insights, SLA, custom domain | Paid; see `docs/PROMOTION.md` |
| 90% reviewer agreement and 25% time-saving targets | Measured later with real reviewers and the AI model |

## UI must never contain
Approve, grant access, payment or self-sign-up controls. Permanent banner: *"This checks request readiness. A ready review is not an access decision."* (changed 16 Oct 2026 for Phase 2b: with auto-assign on, the second Reviewer's acceptance — not the readiness check — leads to an Entra assignment).
