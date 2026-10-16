# SCOPE — PMO Request Review, Phase 1 Pilot

**Status:** Draft. The "Final Power Apps Build Prompts (LOCKED v1.0, 9 Oct 2026)" pack was **not attached** to the build session. This table is built from the pilot prompt (v1.0, 16 Oct 2026) only. It must be reconciled line by line against the locked pack before plan approval (Q7).

The pilot checks whether an onboarding/access request is ready for human review. It **does not approve, route, fulfil or grant anything.**

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
| Approval routing / approver workflow | Out of scope |
| Identity lookup or creation, Microsoft Graph | Out of scope |
| Group assignment, credential issue | Out of scope |
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
Approve, grant access, payment or self-sign-up controls. Permanent banner: *"This checks request readiness. It does not approve or grant access."*
