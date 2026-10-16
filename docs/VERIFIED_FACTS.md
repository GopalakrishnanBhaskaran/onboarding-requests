# Verified Facts

Every free-tier limit, runtime and platform behaviour this pilot depends on. Nothing here is assumed true until its **Status** says so.

**Phase 0 note (16 Oct 2026):** `learn.microsoft.com` is blocked by the build environment's network policy. Facts below were taken from web-search extracts of the Microsoft Learn pages, not from a full page read. Status `SEARCH` means "seen in search extract, must be re-read on the live page before Phase 6". Status `CONFLICT` means it differs from the pilot prompt and needs a decision.

| # | Fact | Value found | Source URL | Checked | Status |
|---|---|---|---|---|---|
| F1 | SWA managed Functions trigger types | HTTP triggers and bindings only. No Durable Functions. | https://learn.microsoft.com/azure/static-web-apps/apis-functions | 2026-10-16 | SEARCH |
| F2 | SWA managed Functions managed identity | Not supported. Key Vault references not supported. | https://learn.microsoft.com/azure/static-web-apps/apis-functions | 2026-10-16 | SEARCH |
| F3 | SWA API request timeout (all backends) | **45 seconds** max per API request. Gateway returns 500; function may keep running. | https://learn.microsoft.com/azure/static-web-apps/apis-overview | 2026-10-16 | SEARCH — **CONFLICT** with 60 s model budget in prompt (see Q3) |
| F4 | SWA managed Functions .NET runtime | Search extract showed `dotnet:8.0` (in-process) and isolated 6.0/7.0 (retired 30 Apr 2025). `dotnet-isolated:8.0` seen in use (GitHub issue, Jun 2026) but **not confirmed in the docs table**. No newer isolated version confirmed. | https://learn.microsoft.com/azure/static-web-apps/languages-runtimes | 2026-10-16 | **UNVERIFIED — blocker** (see Q1) |
| F5 | Azure Functions in-process .NET model | End of support **10 Nov 2026**. | https://azure.microsoft.com/updates/retirement-support-for-the-inprocess-model-for-net-apps-in-azure-functions-ends-10-november-2026 | 2026-10-16 | SEARCH |
| F6 | .NET 8 end of support | **10 Nov 2026** (25 days from today). .NET 10 LTS supported to 14 Nov 2028. | https://learn.microsoft.com/azure/azure-functions/migrate-dotnet-to-isolated-model | 2026-10-16 | SEARCH — **CONFLICT** (see Q1) |
| F7 | SWA Free plan quotas | 10 apps/subscription, 250 MB storage/app, 100 GB bandwidth/month, 3 preview envs, 2 custom domains, 30 MB request size. | https://learn.microsoft.com/azure/static-web-apps/quotas | 2026-10-16 | SEARCH |
| F8 | SWA Free: managed APIs | Managed Functions available on Free. Bring-your-own Functions needs Standard. | https://learn.microsoft.com/azure/static-web-apps/plans | 2026-10-16 | SEARCH |
| F9 | SWA role assignment via serverless function (custom roles) | Not available on Free. Standard only. | https://learn.microsoft.com/azure/static-web-apps/quotas | 2026-10-16 | SEARCH — pilot does not depend on it (app roles live in `userAccess`) |
| F10 | SWA pre-configured Entra ID provider | Allows **any Microsoft account** to sign in (not tenant-restricted). Tenant restriction needs a custom provider; registering one disables all pre-configured providers. | https://learn.microsoft.com/azure/static-web-apps/authentication-custom | 2026-10-16 | SEARCH |
| F11 | SWA custom auth provider plan | Search extract of the auth article says "all features in this article are available in all plans"; the plans page historically lists custom authentication as Standard. | https://learn.microsoft.com/azure/static-web-apps/authentication-authorization , https://learn.microsoft.com/azure/static-web-apps/plans | 2026-10-16 | **UNVERIFIED** — pilot design does not depend on it |
| F12 | SWA client principal | Header `x-ms-client-principal` (base64 JSON: `identityProvider`, `userId`, `userDetails`, `userRoles`). `userId` is a SWA-issued id per app, not the Entra object id. | https://learn.microsoft.com/azure/static-web-apps/user-information | — | **UNVERIFIED** (from prior knowledge; re-read) |
| F13 | Cosmos DB free tier allowance | First 1000 RU/s and 25 GB free per account; usage above is billed. | https://learn.microsoft.com/azure/cosmos-db/free-tier | 2026-10-16 | SEARCH |
| F14 | Cosmos DB free tier eligibility | Provisioned and autoscale accounts. **Not serverless.** | https://learn.microsoft.com/azure/cosmos-db/free-tier | 2026-10-16 | SEARCH |
| F15 | Cosmos DB shared-throughput database | Up to 1000 RU/s free; max 25 containers per shared database. Pilot uses 7. | https://learn.microsoft.com/azure/cosmos-db/free-tier | 2026-10-16 | SEARCH |
| F16 | Cosmos DB free tier: one per subscription; set only at creation | Not confirmed by search extract. | https://learn.microsoft.com/azure/cosmos-db/free-tier | 2026-10-16 | **UNVERIFIED** |
| F17 | Cosmos DB unique keys | Scoped to a logical partition; defined only at container creation. | https://learn.microsoft.com/azure/cosmos-db/unique-keys | — | **UNVERIFIED** (prior knowledge) |
| F18 | Azure Policy built-in "Allowed resource types" | Mode **Indexed**: only resource types that support tags and location are evaluated. Behaviour for Cosmos child types (`sqlDatabases`, `containers`) and SWA child types not confirmed. | https://learn.microsoft.com/azure/governance/policy/tutorials/disallowed-resources | 2026-10-16 | SEARCH — see Q5 |
| F19 | Cost Management budget currency | Budget amount is in the billing account currency. ₹1 only works if billing currency is INR. | https://learn.microsoft.com/azure/cost-management-billing/costs/tutorial-acm-create-budgets | 2026-10-16 | **UNVERIFIED** — see Q4 |

## Re-verification rule
Before Phase 6 what-if, every row must be `VERIFIED` (live page read, date recorded) or explicitly accepted by Gokul.
