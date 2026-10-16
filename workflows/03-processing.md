# 03 — Processing

**Purpose:** Run reviews with truthful statuses under duplicates, concurrency, edits, timeouts and retries.

**Inputs:** Engine (Phase 2); `VERIFIED_FACTS` F3 (45 s gateway limit).

**Steps**
1. `IReviewDispatcher`; `InlineDispatcher` active; `QueueDispatcher` stub (throws if enabled without paid flag).
2. `POST /api/requests/{id}/analyze`: authorise → read request + ETag → snapshot + hash → create run `Queued` with `idempotencyKey = requestId:revision:nonce` → claim with ETag → `Running` → engine → validate → `Completed` / `Failed`.
3. Before commit, re-read request revision. Changed → `Superseded`, keep history.
4. Duplicate (same key) → return the existing run id; never a second Completed.
5. Retry → new run with `retryOfRunId`.
6. `POST /api/operator/runs/recover`: Operator only; `Running` older than N minutes → `Failed`, `errorCategory=Timeout`.
7. Processing budget constant (default 30 s) below the 45 s gateway cut-off.

**Outputs:** dispatcher, endpoints, tests.

**Validation (exit gate):** duplicate, concurrent analyze, edit-during-processing, timeout and retry tests leave truthful statuses.

**Failure handling:** ETag conflict on claim → another worker owns it; return existing run. Any exception → `Failed` with safe `errorCategory` (no stack traces, no request text in logs).
