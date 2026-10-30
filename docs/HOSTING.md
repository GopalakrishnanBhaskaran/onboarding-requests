# HOSTING — where v1.1's Graph and ARM calls run (P3 checkpoint, 30 Oct 2026)

Question from the v1.1 prompt (section 7): is a bring-your-own Function App with managed identity required for F2 (TAP mail) and F5 (offboarding), what does it cost, and what is the alternative?

## Short answer
**No new Function App is needed.** The pilot already runs one: the AI agent worker on Flex Consumption with a system-assigned managed identity (approved 16 Oct 2026). It already makes every Entra and Azure call (Graph reads, v2.1 creates, the agent TAP, role assignments, ACS mail). F2 and F5 run there too. The Static Web Apps managed API stays without a managed identity and never calls Graph or ARM (F85).

What a bring-your-own (BYO) backend would add is **synchronous** Graph/ARM calls from the API (an offboarding plan returned in the same HTTP call) and a reachable host for the **external upload API**. Neither is needed for the pilot.

## Facts this rests on
- SWA managed Functions: HTTP only, no managed identity, no Key Vault references, not callable outside SWA (F85).
- BYO Functions behind SWA needs the **Standard** plan; the `/api` prefix stays; 45 s per request; linking adds an "Azure Static Web Apps (Linked)" identity provider to the Function App (F86, F87).
- Flex Consumption bills on-demand executions with a monthly free grant (F93). The worker today: a 5-minute timer (~8,640 runs a month), cost ₹0.0 so far (`docs/COSTS.md`).
- CLAUDE.md rule 2: no further Function Apps, no Key Vault, no App Service other than the Flex plan.

## Options

| | A. Existing worker, asynchronous (recommended for the pilot) | B. SWA Standard + the existing worker linked as the API backend | C. Actual tenant target: App Service (design of 23 Oct 2026) |
|---|---|---|---|
| How | The API stores the request (an offboarding run, a TAP mail to send); the worker picks it up on its next run (≤ 5 min) and writes results back. The page shows `Planning` / `Executing` and refreshes. | The SWA plan goes Free → Standard; the API code moves into the worker Function App (same app, so still one Function App) and SWA forwards `/api` to it. The API then has the managed identity: plan/execute can answer in the same call (still ≤ 45 s). | One App Service app hosts the website and the API with a managed identity, private endpoint only (`docs/ARCHITECTURE.md` section 12, `docs/SECURE_HOSTING_AND_MAIL.md`). |
| New Azure resources | None (8 Cosmos containers + worker Cosmos roles are needed in every option) | None new; the SWA plan changes | App Service plan + app, private endpoints, VNet, Key Vault (needs rule changes) |
| Extra cost | ≈ ₹0: a few hundred more executions and Graph calls a month, inside the Flex free grant (F93) | SWA Standard plan price (**not on Learn — check the pricing calculator before approval**, F87) + worker executions for API calls | App Service plan + networking (enterprise budget) |
| Offboarding contract | `POST …?mode=plan` returns the run at once with status `Planning`, then `Planned` once the worker read Entra/ARM; the same for `execute` (`Executing` → `Completed` / `PartiallyCompleted` / `Failed`). Decision 2 (30 Oct) allows these two extra statuses. | `Planned` / `Completed` … in the same call when it fits in 45 s; otherwise as A | as B (no 45 s SWA gateway limit) |
| External upload API (F4) | Built and **off**: SWA's sign-in sits in front of `/api`, and it is not stated on Learn whether a bearer token reaches the managed API (open item in VERIFIED_FACTS). | Reachable on the Function App's own host (`https://func-…azurewebsites.net/api/v1/uploads/…`) with Entra token validation in code; the "Linked" identity provider must allow it — prove with a dry run. | Reachable on the app's private endpoint for internal callers. |
| CLAUDE.md fit | Fits every rule as it stands | Needs Gokul's written approval of the SWA plan change (cost) | Needs approval of several "never create" items |
| Rollback | Switch `AGENT_OFFBOARDING` off; nothing to undo in Azure | Unlink the backend in SWA → *APIs*, redeploy managed API, plan back to Free (F86: the Linked identity provider stays on the Function App until removed) | per enterprise runbook |

## Recommendation
**A for the pilot and for building/testing v1.1** (P4–P6 are code-only and mocked anyway). Move to **B** only if PMO needs the offboarding plan inside the same click or the external upload API must go live before the actual-tenant target; move to **C** with the actual-tenant rollout.

## What A means in code (P4/P5)
- F2: the TAP mail stays inside the worker run that creates the account (as today) — no API involvement, recipient from the people inventory + Entra cross-check.
- F5: `POST /api/offboarding/{enterpriseId}?mode=plan` (PMO/Operator) creates an `offboardingRuns` record (`Planning`); the worker resolves the user and reads groups, methods and RBAC, then sets `Planned` with the steps. `mode=execute` (a second person in a production tenant) records the confirmation bound to the plan hash; the worker executes, verifies, sets the final status, and only then mails PMO. Each step is idempotent, so a repeat run or a double click changes nothing twice.
- Worker trigger: the existing 5-minute timer. (An HTTP trigger on the worker would need a function key in the SWA settings; not proposed.)
