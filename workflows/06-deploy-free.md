# 06 — Deploy to Azure (free)

**Purpose:** Deploy the pilot at ₹0 with guardrails, only after explicit approval.

**Inputs:** Subscription ID, region, budget email, Phase 5 report all-critical-pass, `VERIFIED_FACTS` all `VERIFIED`.

**Steps**
1. `tools/check_free_tier.sh <sub>` — no existing Cosmos free-tier account. If one exists: **stop and ask.**
2. `az deployment group what-if` for `infra/main.bicep`.
3. Present what-if, resource list, expected cost (₹0 with reasons). **Wait for "approved".**
4. Deploy in order: RG → policy assignment → budget → Cosmos (free tier) → SWA Free.
5. Set Cosmos connection string as SWA app setting via `az staticwebapp appsettings set` (never committed, never echoed).
6. Deploy app with SWA CLI. Record commit SHA.
7. Seed SAMPLE data; test users sign in once, then register their SWA `userId` in `userAccess`.
8. Run one synthetic live request; capture result.
9. `tools/cost_check.sh`; repeat after 24–48 h.

**Outputs:** app URL, commit, live result, policy + budget IDs, cost output.

**Validation:** only allowed resource types in RG; cost ₹0 after 24–48 h.

**Failure handling:** any non-allowed resource appears → delete it, report. Any non-zero cost → follow `incident-recovery.md`.
