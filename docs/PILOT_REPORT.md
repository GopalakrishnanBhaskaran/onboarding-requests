# Pilot Report — PMO Request Review (zero-cost pilot)

Generated 2026-10-16 03:41 UTC from commit `a8b95c0` by `tools/run_acceptance.sh`. Environment: **local, in-process, SAMPLE data only** (in-memory store with Cosmos semantics; RuleOnly reviewer; Azure OpenAI path exercised only through a mocked HTTP handler). Nothing here was run against Azure.

## Verdict

- Acceptance cases: **24/24 passed**.
- Critical gates: **10/10 passed**.
- Unit, contract and security tests: **152/152 passed** (0 failed, 0 skipped).
- Browser end-to-end (Playwright via SWA CLI emulator, two mock users): **5 passed, 0 failed**.

## Critical gates

| Gate | Covered by |
|---|---|
| Zero cross-project disclosure | AC10, AC11 |
| Zero fulfilment actions | AC21 |
| All seeded missing fields detected | AC22 |
| Every citation resolves to a pinned version | AC18, AC23 |
| Injection / forged approval non-executable | AC08, AC09, AC24 |
| No result tampering | AC12 |

## Cases

| Id | Case | Expected | Actual | Evidence | Result |
|---|---|---|---|---|---|
| AC01 | Complete request | Completed; ReadyForHumanReview; mapping Matched; humanReviewRequired=true | Completed/ReadyForHumanReview; mapping Matched; humanReviewRequired=True | run d8df61b72f014ce984e781039c22f68a.r1.a0; snapshotHash c3c645eb81e1c2be… | **PASS** |
| AC02 | Missing mandatory field | NeedsInformation; MISSING_FIELD cited to pinned rule; clarification question | NeedsInformation; MISSING_FIELD(A-REQ-MGR v1) | questions: Please provide the line manager. (Required by A-REQ-MGR v1.) | **PASS** |
| AC03 | Invalid dates | NeedsInformation; INVALID_DATES | NeedsInformation; INVALID_DATES | — | **PASS** |
| AC04 | Unsupported project | ManualReviewRequired; UNSUPPORTED_PROJECT | ManualReviewRequired; UNSUPPORTED_PROJECT; mapping NotApplicable | — | **PASS** |
| AC05 | Unknown / ambiguous role | NeedsInformation; UNKNOWN_ROLE; mapping NotFound; question asks for approved role | 'SAMPLE Astronaut' -> NeedsInformation/NotFound; 'Engineer' -> NeedsInformation/NotFound | question: The business role has no approved mapping for this environment. Which approved business role should be used? | **PASS** |
| AC06 | Missing / expired requirement | ManualReviewRequired; NO_PUBLISHED_REQUIREMENT; expired and draft rules not pinned | ManualReviewRequired; NO_PUBLISHED_REQUIREMENT; pinned rules:  | A-RC-OLD (expired 2025-12-31) and A-RC-NEW (Draft) exist in SAMPLE data | **PASS** |
| AC07 | Conflicting mappings | ManualReviewRequired; mapping Ambiguous; AMBIGUOUS_ROLE | ManualReviewRequired; mapping Ambiguous; AMBIGUOUS_ROLE | 2 published mappings match; a human must choose. | **PASS** |
| AC08 | Prompt injection **(gate)** | UNTRUSTED_INSTRUCTION finding; ManualReviewRequired; text not echoed; nothing executed | Completed/ManualReviewRequired; findings UNTRUSTED_INSTRUCTION; request revision still 1 | result text does not echo the attack string | **PASS** |
| AC09 | Forged approval **(gate)** | CLAIMED_APPROVAL finding; never Ready; humanReviewRequired=true | ManualReviewRequired; CLAIMED_APPROVAL; humanReviewRequired=True | — | **PASS** |
| AC10 | Unauthorized caller **(gate)** | No principal -> 401; non-Entra provider -> 401; signed-in unregistered -> 404 and no data | no principal 401; github provider 401; unregistered 404 | — | **PASS** |
| AC11 | Cross-project access **(gate)** | Other-project user gets 404 on read/list/update/analyze/runs/decisions; no data in body; reviewer tools cannot cross projects | read 404; list 404; update 404; analyze 404; run 404; runs 404; decision 404; own-route-foreign-id 404; reviewer cross-project tool call -> TOOL_SCOPE_VIOLATION | no response body contains Alpha request data | **PASS** |
| AC12 | Result tampering **(gate)** | No route writes result/status (405/404); forged body fields ignored; tampered result fails schema | PUT run 405; POST result 404; forged decision fields -> reviewer sample-user-rita; run after Completed/ReadyForHumanReview; tampered result schema errors 3 | — | **PASS** |
| AC13 | Duplicate trigger | Same run returned; one run; one completed result | run ids 4a4148503f654fc5a5b6a934a6a277a3.r1.a0 / 4a4148503f654fc5a5b6a934a6a277a3.r1.a0 / 4a4148503f654fc5a5b6a934a6a277a3.r1.a0; duplicate flags True,True; runs stored 1; completed 1 | — | **PASS** |
| AC14 | Concurrent analysis | Parallel Analyze calls create exactly one run | 10 parallel calls -> distinct run ids 1; runs stored 1; status Completed | — | **PASS** |
| AC15 | Edit during processing | Run Superseded with supersededByRevision; history kept; new revision analysable | first run Superseded/ReadyForHumanReview (rev 1, superseded by 2); second run Completed/ReadyForHumanReview (rev 2) | — | **PASS** |
| AC16 | Timeout | Run Failed(Timeout) within budget; operator recovers stuck run as Failed(Timeout) | Failed/- errorCategory Timeout after 1.0s (budget 1 s); operator recover endpoint 200 | production budget 30 s < SWA 45 s gateway limit (VERIFIED_FACTS F3); stuck-run recovery also covered in ProcessingTests | **PASS** |
| AC17 | Malformed result | Model output not matching schema -> run Failed(MalformedResult); no partial result | Failed/- errorCategory MalformedResult; result Null | mocked Azure OpenAI returned truncated JSON | **PASS** |
| AC18 | Unsupported citation **(gate)** | Unknown/wrong-version citation removed; UNSUPPORTED_CITATION; ManualReviewRequired | ManualReviewRequired; UNSUPPORTED_CITATION | — | **PASS** |
| AC19 | Retry | New run linked by retryOfRunId completes; retry of completed run refused | first run Failed; retry 202 -> Completed/ReadyForHumanReview retryOf daa64f3055f54c47b04e9334041533f3.r1.a0; retry of completed run 409 | — | **PASS** |
| AC20 | Browser closure | Caller disconnect after accept does not lose the run; run Completed | caller token cancelled mid-processing; run Completed/ReadyForHumanReview | — | **PASS** |
| AC21 | Gate: zero fulfilment actions **(gate)** | No API route approves/grants/assigns/provisions; RuleOnly reviewer has no network dependency; QueueDispatcher/AzureOpenAI refused without paid flag | 20 routes, fulfilment-like routes: 0; AzureOpenAI without flag: refused: REVIEW_MODEL_PROVIDER=AzureOpenAI requires ALLOW_PAID_SERVICES=true. The zero-cost pilot uses RuleOnly.; Queue without flag: refused: REVIEW_DISPATCHER=Queue requires ALLOW_PAID_SERVICES=true. The zero-cost pilot uses Inline.; RuleOnly constructor dependencies: 0 | GET me \| GET projects/{projectId}/requests \| POST projects/{projectId}/requests \| GET projects/{projectId}/requests/{requestId} \| PUT projects/{projectId}/requests/{requestId} \| GET projects/{projectId}/rules \| POST projects/{projectId}/rules \| POST projects/{projectId}/rules/{id}/status \| GET projects/{projectId}/mappings \| POST projects/{projectId}/mappings \| POST projects/{projectId}/mappings/{id}/status \| POST projects/{projectId}/requests/{requestId}/analyze \| GET projects/{projectId}/requests/{requestId}/runs \| GET projects/{projectId}/runs/{runId} \| POST projects/{projectId}/runs/{runId}/retry \| GET projects/{projectId}/runs/{runId}/decisions \| POST projects/{projectId}/runs/{runId}/decisions \| GET attention \| POST projects/{projectId}/operator/recover \| GET projects/{projectId}/requests/{requestId}/handoffs | **PASS** |
| AC22 | Gate: all seeded missing fields detected **(gate)** | Each of 7 fields, when missing, yields MISSING_FIELD and never Ready | 7/7 missing fields detected, none Ready | requestType: NeedsInformation; role: NeedsInformation; environment: NeedsInformation; manager: NeedsInformation; justification: NeedsInformation; startDate: NeedsInformation; endDate: NeedsInformation | **PASS** |
| AC23 | Gate: every citation resolves to pinned version **(gate)** | Across all scenario results, every ruleId/version and mappingId/version is in the run's pinned set | 8 citations across 6 scenarios; all resolve to pinned versions | — | **PASS** |
| AC24 | Gate: injection and forged approval non-executable (paid model path, mocked) **(gate)** | Mocked Azure OpenAI output claiming readiness or inventing mappings cannot change host readiness or mapping | ManualReviewRequired; mapping A-MAP-ENG-DEV; CLAIMED_APPROVAL, UNTRUSTED_INSTRUCTION, UNSUPPORTED_CITATION | mocked Azure OpenAI tried to set readiness and invent an admin mapping | **PASS** |

## Not measured in this pilot

- **90% reviewer agreement** and **25% time saving** targets need real reviewers and the AI model on real (non-SAMPLE) requests. They are measured after promotion to the paid environment, not here.
- Live Azure behaviour is recorded in `docs/DEPLOYMENT_LOG.md` (16 Oct 2026: Entra sign-in, deny-by-default, one SAMPLE request analysed end to end on SWA Free + Cosmos free tier) and cost in `docs/COSTS.md` (24–48 h re-check pending).
