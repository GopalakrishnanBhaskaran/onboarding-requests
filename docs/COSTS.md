# Costs — zero-cost pilot

Status: **infrastructure deployed and app uploaded 16 Oct 2026; live check and 24–48 h cost re-check pending.** Evidence below comes from `tools/cost_check.sh`; re-check 24–48 h after the app goes live.

## Why the design should cost ₹0 (to be proven, not assumed)

| Resource | Free basis | Guardrail in code/infra |
|---|---|---|
| Static Web Apps | Free plan (F7, F8) | `sku: Free` in Bicep; test `InfraConsistencyTests` |
| SWA managed Functions | included in Free plan (F8) | HTTP only; no Function App, no storage account |
| Cosmos DB for NoSQL | free tier: first 1000 RU/s + 25 GB per account, one account per subscription (F13–F16) | `enableFreeTier: true`, shared DB `throughput: 1000`, account `totalThroughputLimit: 1000`, no autoscale, single region, no zone redundancy, no analytical store |
| Azure Policy assignment | no charge for built-in policy assignment | allowed types limited to SWA + Cosmos (+ child types) |
| Cost Management budget | no charge | ₹1 monthly, alerts at >1% actual and forecast |
| Azure OpenAI, App Insights, Key Vault, Storage, Function App, Service Bus | **not created** | `ALLOW_PAID_SERVICES=false`; startup refuses paid providers |

## Phase 2 (approved 16 Oct 2026, paid from Azure credits)

| Resource | Expected monthly cost | Notes |
|---|---|---|
| Flex Consumption Function App (worker) | ~₹0–100 | Timer every 5 min ≈ 8,640 short runs/month, 512 MB; Flex has a monthly free grant (verify on the bill) |
| Storage account (Standard_LRS) | ~₹10–100 | App package + host locks only; transactions from the timer |
| Graph API calls | ₹0 | Included with the tenant |
| Entra ID P2 / Governance | ₹0 during the 30-day trial | Not an Azure charge; **credits do not cover it** (F28). Decide before the trial ends |
| **Budget** | **₹2,000 cap** | Alerts at 50% / 80% / 100% actual and 100% forecast |

Stop all Phase 2 cost: `az functionapp stop -g rg-pmo-review-pilot -n <worker>` (or delete the worker + its storage). The review app keeps working.

## Things that could still cost money (watch list)

- Cosmos storage above 25 GB or throughput above 1000 RU/s — capped by `totalThroughputLimit`; storage is tiny with SAMPLE data.
- Cosmos periodic backup: default backup storage redundancy (Geo) — verify that the two default backup copies are free (F22) before deploy.
- SWA bandwidth above the Free allowance (100 GB/month, F7) — not plausible for a pilot.
- Anyone creating extra resources in the RG — blocked by policy (allow ~30 min for a new assignment to take effect) and flagged by `cost_check.sh`.

## Evidence log

| Date (UTC) | Check | Result | By |
|---|---|---|---|
| 2026-10-16 | after infra deploy (app not yet deployed) | Only SWA (Free) + Cosmos (free tier, 1000 RU/s, no autoscale) in RG; policy enforcing; budget spent **0.0 INR**; Cost Management query not yet available (NotFound) | Gokul |
| 2026-10-16 02:58 | after app redeploy #3 | budget `pmo-pilot-zero-cost` 1.0 INR, spent **0.0 INR**; Cost Management RG query still NotFound (new subscription) | Gokul |
| — | +24–48 h (on or after 2026-10-17 03:00 UTC) | pending | — |
| 2026-10-16 16:01 | `cost_check.sh` after Phase 2 stage B | Resources: Cosmos (free tier, 1000 RU/s, autoscale null), SWA Free, FC1 plan, Standard_LRS storage, Function App. Budget ₹2,000, spent ₹0.0. MTD: Cosmos ₹0.0, SWA ₹0.0 (worker costs not yet reported; data lags 24–48 h). Re-check 17–18 Oct. | Gokul |
| 2026-10-16 16:58 | `cost_check.sh` after Phase 2b apply | Same 5 resources; OK only allowed types; budget ₹2,000, spent ₹0.0; MTD Cosmos ₹0.0, SWA ₹0.0 (worker not yet reported, lag 24–48 h). | Gokul |
| 2026-10-16 19:14 | `cost_check.sh` after Phase 2c apply | Same 5 resources; OK only allowed types; budget ₹2,000, spent ₹0.0; MTD Cosmos ₹0.0, SWA ₹0.0 (worker costs lag 24–48 h). | Gokul |
| 2026-10-21 23:43 | `cost_check.sh` after the v2.2 `app` redeploy and mail setup | Resources: Cosmos (free tier, 1000 RU/s), SWA Free, FC1 plan, Standard_LRS storage, worker Function App, Communication Services `acs-pmo-…`, Email Communication Service `ecs-pmo-…` + `AzureManagedDomain` (mail: pay per mail, F64). Budget ₹2,000, spent ₹2.90; MTD storage ₹2.90, Cosmos/Function App/SWA ₹0.0; mail cost not reported yet (lag 24–48 h). The domain child type was flagged by a script bug (fixed). | Gokul |
| 2026-10-22 | agent TAP, form, import (code only) | No new resource. The agent TAP adds one Graph permission and one mail per new account (ACS pay per mail, ~₹0 at pilot volume). | Claude (estimate) |
