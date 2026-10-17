# 03 — Processing and the agent step (v2)

**Purpose:** Truthful review statuses under duplicates, concurrency, edits, timeouts and retries; one idempotent agent step that moves a request through the flow.

**Inputs:** Engine (02); `VERIFIED_FACTS` F3 (45 s gateway limit).

**Steps**
1. `Processing/RunService.cs`: deterministic run id per request revision and trigger; `idempotencyKey` unique; `InlineDispatcher` active, `QueueDispatcher` coded (paid flag required).
2. `Processing/ReviewProcessor.cs`: claim with ETag → `Running` → engine → validate → `Completed` / `Failed`; revision changed before commit → `Superseded`. Processing budget 30 s (< 45 s).
3. `Workflow/WorkflowAgent.cs` `AdvanceAsync`: computes one step from stored state; writes the request (ETag) first, then idempotent effects (notifications, issues, ADO) keyed `requestId:step`. Called inline by the API after every action and by the worker timer every 5 minutes.
4. Safe auto-fixes only: retry max 3, reuse an existing group, fix ID/name format, refresh the sheet read; then Failed-Needs-Dev with a developer report. Operator **Resume after fix** sends it back to the failed stage.
5. SLA: reminder at SLA, escalation to the manager at 2× SLA (Graph manager or project contact). Never skips a stage, never auto-approves.
6. Operator `POST …/operator/recover`: runs stuck in Queued/Running past `STUCK_RUN_MINUTES` → Failed (Timeout).

**Outputs:** run service, processor, agent, endpoints.

**Validation (exit gate):** `ProcessingTests`, `AgentTests`, `FlowTests`; AC13, AC18, AC22.

**Failure handling:** ETag conflict → another caller owns the step; reload and stop. Any exception → `Failed` with a safe category (no stack traces, no request text in logs).
