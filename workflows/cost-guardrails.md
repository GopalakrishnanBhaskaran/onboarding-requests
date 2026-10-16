# Cost guardrails

**Purpose:** Keep the pilot at ₹0 and detect drift fast.

**Inputs:** RG `rg-pmo-review-pilot`; budget email.

**Steps (always on)**
1. Dedicated RG only. No resources outside it.
2. Policy on RG: allowed types = `Microsoft.Web/staticSites`, `Microsoft.DocumentDB/databaseAccounts` (+ required child types, see Q5 in `docs/PHASE0_PLAN.md`).
3. Budget on RG: smallest amount in billing currency (₹1 if INR), alert at 1% actual.
4. App flag `ALLOW_PAID_SERVICES=false`; startup throws if a paid provider is selected.
5. Cosmos: free tier on at creation; one shared database ≤ 1000 RU/s; no autoscale above it; no analytical store; single region; no zone redundancy.
6. SWA: Free SKU; no custom domain needed.
7. Before any infra change: what-if + approval.

**Outputs:** `tools/cost_check.sh` output in `docs/COSTS.md` after deploy and after 24–48 h.

**Validation:** cost check shows only allowed types and ₹0.

**Failure handling:** see `incident-recovery.md`. Cost data lags up to 24–48 h; a ₹0 reading on day 0 is not proof.
