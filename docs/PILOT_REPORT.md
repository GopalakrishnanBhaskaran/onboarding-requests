# Pilot Report — PMO onboarding flow (v2)

Generated 2026-10-17 03:56 UTC from commit `8aafffc` by `tools/run_acceptance.sh`. Environment: **local, in-process, SAMPLE data only** (in-memory store with Cosmos semantics; RuleOnly reviewer; Azure OpenAI path exercised only through a mocked HTTP handler). Nothing here was run against Azure.

## Verdict

- Acceptance cases: **27/27 passed**.
- Critical gates: **12/12 passed**.
- Unit, contract and security tests: **204/204 passed** (0 failed, 0 skipped).
- Browser end-to-end (Playwright via SWA CLI emulator, one mock user per role): **6 passed, 0 failed**.

## Critical gates

| Gate | Case | Result |
|---|---|---|
| Incomplete request rejected | AC02 | **PASS** |
| Mapping gap returned, never sent for approval | AC05 | **PASS** |
| Partially reviewed request never sent for approval | AC09 | **PASS** |
| Unknown approver ignored, logged, Security alerted | AC10 | **PASS** |
| Owner then Security, separate people | AC11 | **PASS** |
| SLA reminder then escalation, never auto-approval | AC13 | **PASS** |
| ID team task and TAP rules | AC14 | **PASS** |
| TAP never accepted or stored | AC15 | **PASS** |
| Mapping change after review re-runs the review | AC18 | **PASS** |
| Agent never approves, creates or assigns | AC23 | **PASS** |
| Prompt injection | AC24 | **PASS** |
| Cross-project isolation | AC25 | **PASS** |

## Cases

| Id | Case | Expected | Actual | Evidence | Result |
|---|---|---|---|---|---|
| AC01 | Flow overview stated upfront | Submit response and notification carry the 7-step flow and who does what | response flowOverview 7 steps; notification to sample-user-alice@sample.invalid | Your onboarding request for Sample Joiner (sample.e1001@sample.invalid): 'User' on 'SAMPLE Databricks Engineering' was received.; ; Flow: | **PASS** |
| AC02 | Incomplete request rejected **(gate)** | 400 incomplete_request listing exactly the missing fields; nothing saved | 400 incomplete_request: First name, Last name, Access level, Justification, Owner | saved requests: 0 | **PASS** |
| AC03 | Domain-specific ID derived; ID/name format fixed | domainId = normalised Enterprise ID + project domain; format fixes logged | domainId sample.e7007@sample.invalid; names Sample Joiner | — | **PASS** |
| AC04 | Review pass goes to the Owner with the AI summary | Awaiting Owner; notification to the named Owner with what, why, group, role, risk flags, review results | AwaitingOwner; to sample-user-olivia@sample.invalid | What: Sample Joiner (sample.e1001@sample.invalid) requests 'User' on 'SAMPLE Databricks Engineering'.; Why: SAMPLE: joins the data engineering team to build and run ingestion pipelines in the dev workspace.; Group: SAMPLE-GRP-ALPHA-DBW-ENG-USER.; Role: SAMPLE Workspace User at SAMPLE dbw-alpha-dev.; Risk flags: none.; Review: Pass — 20/20 checks passed. | **PASS** |
| AC05 | Mapping gap returned, never sent for approval **(gate)** | Returned to requester and PMO with specific reasons; no approval request | Returned; to sample-user-alice@sample.invalid,sample.pmo.alpha@sample.invalid | Mapping sheet: The mapping sheet has no row for resource 'SAMPLE Unknown System'. | **PASS** |
| AC06 | Mapping sheet checks | Duplicate/conflicting rows, deprecated row, bad group name, unflagged privileged role each FAIL with a reason | Fail; failed REQ_PRIVILEGED_FLAGGED | Request vs mapping: Role Owner ('Admin') is privileged but row A-BILLING-ADMIN is not flagged Privileged. PMO must flag it. | **PASS** |
| AC07 | Response document checks | Existing duplicate and conflicting assignment FAIL | duplicate: RESP_NO_DUPLICATE; conflict: RESP_NO_CONFLICT | — | **PASS** |
| AC08 | Risk flags recorded | Privileged, guest, existing broad access, sensitive resource flagged; review still Pass | Pass; flags Privileged access, Guest user, Existing broad access, Sensitive resource | — | **PASS** |
| AC09 | Partially reviewed request never sent for approval **(gate)** | Entra cross-check Pending keeps the request in AI Review; no approval request | AiReview; run outcome Pending; ENTRA_GROUP Pending | — | **PASS** |
| AC10 | Unknown approver ignored, logged, Security alerted **(gate)** | 403 not_an_approver; no approval recorded; High issue; alert to Security | 403 not_an_approver; status AwaitingOwner; issue High; alert to sample.security.alpha@sample.invalid | — | **PASS** |
| AC11 | Owner then Security, separate people **(gate)** | Security before Owner refused; same person refused; separate Security moves to ID team | security first: 403; same person: 409 same_approver; separate security: 200; now WithIdTeam | — | **PASS** |
| AC12 | Rejection captures remarks, notifies, stops | Rejected; requester and PMO notified with remarks | Rejected; notified sample-user-alice@sample.invalid,sample.pmo.alpha@sample.invalid | Request for Sample Joiner (sample.e1001@sample.invalid): 'User' on 'SAMPLE Databricks Engineering' was rejected by Security (sample-user-sam@sample.invalid) on 2026-10-17 09:00 UTC.; ; Remarks: SAMPLE: risk too high for a guest. | **PASS** |
| AC13 | SLA reminder then escalation, never auto-approval **(gate)** | One reminder, one escalation; still awaiting approval | reminder 1, escalation 1; status AwaitingSecurity | — | **PASS** |
| AC14 | ID team task and TAP rules **(gate)** | Task has user details, groups, approved summary; TAP not one-time / too long / before membership rejected | not one-time 400; 8h 400; before membership 400 | User: Sample Joiner; Enterprise ID sample.e1001; domain-specific ID sample.e1001@sample.invalid.; Group: SAMPLE-GRP-ALPHA-DBW-ENG-USER — exists: use it.; Steps: create the user ID if missing; add the user to the group; then issue a ONE-TIME Temporary Access Pass with the shortest lifetime (at most 60 minutes) and give it to the user directly, separately from the Enterprise ID. Never put the pass in mail, tickets, chat or this app: record only issued time, expiry, one-time flag and issued-by. | **PASS** |
| AC15 | TAP never accepted or stored **(gate)** | Body carrying a pass refused; pass appears nowhere (request, notifications, issues) | 400; pass found anywhere: False | — | **PASS** |
| AC16 | Different group returned to the ID team | Task Returned with the exact difference; GroupNameMismatch issue | Returned | - Expected group SAMPLE-GRP-ALPHA-DBW-ENG-USER (reviewed and approved), reported SAMPLE-GRP-ALPHA-OTHER. Fix the group name, or update the mapping sheet — the request is then reviewed again and needs new approvals. | **PASS** |
| AC17 | Role mismatch returned to the role team | Task Returned (missing/extra role); RoleMismatch issue | Returned | - Reported and recorded: missing SAMPLE Workspace User at SAMPLE dbw-alpha-dev; extra SAMPLE Workspace Admin at SAMPLE dbw-alpha-dev. Correct the assignment or revoke the extra role. | **PASS** |
| AC18 | Mapping change after review re-runs the review **(gate)** | mapping-changed event, new review, continues only if group and role are unchanged | AwaitingSecurity; events mapping-changed > review-start > review-pass | — | **PASS** |
| AC19 | Final verification and notifications | Login, permanent method, TAP deleted -> Completed; user, Owner, Security, PMO, teams notified | Completed; checks FINAL_LOGIN=Pass, FINAL_ACCESS=Pass, FINAL_PERMANENT_METHOD=Pass, FINAL_TAP_DELETED=Pass | notifications: FlowOverview, ApprovalRequested, TeamTask, AccessReady, CompletionSummary, PmoInformed, Closure | **PASS** |
| AC20 | ADO work item with full trail | Work item linked to the request id; description has review, approvals, ID team, role, justification | work item 1000; tag pmo-request-fff408d9625c403ba7db0609ccd18d31 | Onboarding fff408d9: sample.e1001@sample.invalid — User on SAMPLE Databricks Engineering [With Role Team] | **PASS** |
| AC21 | Status line and time per stage | Request ID \| Requester \| stage \| waiting on \| done \| next \| time in stage \| ADO link; wait per team | Awaiting Owner \| waiting Owner \| 5h 0m | Owner=5h 0m; AI agent=0m | **PASS** |
| AC22 | Auto-fix limit then Failed-Needs-Dev | At most 3 retries per step; Failed-Needs-Dev with developer report | FailedNeedsDev; attempts r1=3 | — | **PASS** |
| AC23 | Agent never approves, creates or assigns **(gate)** | No grant/assign/provision route; no Entra write call in source | 24 routes, none grant/assign/provision; Entra write calls in source: 0 | — | **PASS** |
| AC24 | Prompt injection **(gate)** | REQ_TEXT_CLEAN fails; text not echoed; nothing followed | Fail; REQ_TEXT_CLEAN=Fail | Request text in Justification contains instruction-like text or claims an approval. Approvals come only from the Owner and Security in this app; rewrite it. | **PASS** |
| AC25 | Cross-project isolation **(gate)** | Other project gets 404 for view and approval | view 404; approve 404 | — | **PASS** |
| AC26 | Daily summary with wait time per team | Once per day to Operators, PMO, Security; includes wait time per team | to sample.operator.alpha@sample.invalid,sample.pmo.alpha@sample.invalid,sample.security.alpha@sample.invalid | Daily summary SAMPLE-ALPHA — 2026-10-17 (UTC); Submitted 1 · Completed 0 · Rejected 0 · Returned 0 · Failed-Needs-Dev 0; Open by status: Awaiting Security 1; Waiting on: Security 1; Wait time per team today: Security 15h 0m; Average time per stage (completed today): —; SLA breaches today: 0 · Open issues: none;  | **PASS** |
| AC27 | Azure OpenAI contract (mocked) | Structured JSON, temperature 0, secrets masked, 429 backoff, token usage recorded | temperature 0; format json_schema; calls 2; tokens 120+30 | — | **PASS** |

## Not measured in this pilot

- **90% reviewer agreement** and **25% time saving** targets need real reviewers and the AI model on real (non-SAMPLE) requests. They are measured after promotion to the paid environment, not here.
- Live Azure behaviour is recorded in `docs/DEPLOYMENT_LOG.md` (16 Oct 2026: Entra sign-in, deny-by-default, one SAMPLE request analysed end to end on SWA Free + Cosmos free tier) and cost in `docs/COSTS.md` (24–48 h re-check pending).
