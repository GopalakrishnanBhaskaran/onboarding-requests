# 02 — Review engine

**Purpose:** Bounded, deterministic review with a host-validated result contract; paid model coded and disabled.

**Inputs:** Snapshot from Phase 1; published SAMPLE rules/mappings; `review-result.v1.json`.

**Steps**
1. Deterministic checks: mandatory fields, date/type consistency, supported project/environment, published + effective requirement exists.
2. Tools `GetPublishedRequirements` and `GetApprovedRoleMapping`: host-scoped to the snapshot's `projectId`, Published + effective only, return pinned versions.
3. `IReviewModel`; `RuleOnlyReviewModel` (templates, no network).
4. `AzureOpenAIReviewModel`: structured outputs, same two tools, ≤ 4 turns, ≤ 4 tool calls, time budget below the SWA 45 s limit (F3), token limits from config. Tested with a mocked `HttpMessageHandler` only.
5. Startup guard: `REVIEW_MODEL_PROVIDER=AzureOpenAI` and `ALLOW_PAID_SERVICES!=true` → throw.
6. Host step: recompute readiness, reject unknown/unpinned citations, schema-validate, force `humanReviewRequired=true`.
7. Injection handling: instruction-like or "already approved" text → finding `UNTRUSTED_INSTRUCTION` / `CLAIMED_APPROVAL`.

**Outputs:** engine, adapters, schema, `tools/validate_result_schema.py`, tests.

**Validation (exit gate):** contract, citation and injection tests pass for RuleOnly and AzureOpenAI (mock).

**Failure handling:** model output invalid → run `Failed` with `errorCategory=MalformedResult`, never partial results. Missing critical field can never yield `ReadyForHumanReview` — enforce with a test.
