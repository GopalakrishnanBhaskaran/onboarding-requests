# Costs — zero-cost pilot

Status: **not deployed.** No Azure resource exists yet, so there is no cost evidence. This file is filled in from `tools/cost_check.sh` output after the Phase 6 deploy and again 24–48 h later.

## Why the design should cost ₹0 (to be proven, not assumed)

| Resource | Free basis | Guardrail in code/infra |
|---|---|---|
| Static Web Apps | Free plan (F7, F8) | `sku: Free` in Bicep; test `InfraConsistencyTests` |
| SWA managed Functions | included in Free plan (F8) | HTTP only; no Function App, no storage account |
| Cosmos DB for NoSQL | free tier: first 1000 RU/s + 25 GB per account, one account per subscription (F13–F16) | `enableFreeTier: true`, shared DB `throughput: 1000`, account `totalThroughputLimit: 1000`, no autoscale, single region, no zone redundancy, no analytical store |
| Azure Policy assignment | no charge for built-in policy assignment | allowed types limited to SWA + Cosmos (+ child types) |
| Cost Management budget | no charge | ₹1 monthly, alerts at >1% actual and forecast |
| Azure OpenAI, App Insights, Key Vault, Storage, Function App, Service Bus | **not created** | `ALLOW_PAID_SERVICES=false`; startup refuses paid providers |

## Things that could still cost money (watch list)

- Cosmos storage above 25 GB or throughput above 1000 RU/s — capped by `totalThroughputLimit`; storage is tiny with SAMPLE data.
- Cosmos periodic backup: default backup storage redundancy (Geo) — verify that the two default backup copies are free (F22) before deploy.
- SWA bandwidth above the Free allowance (100 GB/month, F7) — not plausible for a pilot.
- Anyone creating extra resources in the RG — blocked by policy (allow ~30 min for a new assignment to take effect) and flagged by `cost_check.sh`.

## Evidence log

| Date (UTC) | Check | Result | By |
|---|---|---|---|
| — | after deploy | not deployed yet | — |
| — | +24–48 h | not deployed yet | — |
