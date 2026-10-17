# 02 — AI review engine (Stage 2, v2)

**Purpose:** Review every request against both sources of truth, read fresh, with a host-validated result. PASS goes to the Owner with an AI summary; FAIL goes back with the exact reasons.

**Inputs:** Request snapshot; `mappingSheet` and `responseDocument` (never cached); `schema/review-result.v2.json`.

**Steps**
1. `Sources/SourcesOfTruth.cs`: read both sources fresh (3 attempts), per-resource sheet fingerprint (SHA-256) stored on the run.
2. `Review/ReviewChecks.cs`: intake complete; mapping row exists / single group+role / no conflict / group naming / not deprecated; response document format / no duplicate / no conflict / role match / justification; request vs mapping (level justified, privileged flagged, text clean — injected instructions or "already approved" claims are findings); Entra cross-check (read-only, or "to be created by the ID team"); risk flags (privileged, guest, broad, sensitive).
3. `IReviewModel`: `RuleOnlyReviewModel` (active) or `AzureOpenAIReviewModel` (off): strict json_schema, temperature 0, secrets/TAP masked before the prompt, back-off on 429, token usage logged. The model can add concerns, never remove a failed check.
4. `Review/ResultAssembler.cs`: outcome Fail > Pending > Pass; `humanApprovalRequired=true`; schema-validated.
5. The SWA API has no managed identity: Entra checks are Pending there and the worker completes them. Never sent for approval while Pending.

**Outputs:** engine, checks, adapters, schema, `tools/validate_result_schema.py`.

**Validation (exit gate):** `ReviewChecksTests`, `AzureOpenAIModelTests` (incl. strict-mode schema rules, F53), `ResultContractTests`; AC04–AC09, AC24.

**Failure handling:** malformed model output → run `Failed` (`MalformedResult`), never a partial result; source read failure → retried, then Failed-Needs-Dev with a developer report.
