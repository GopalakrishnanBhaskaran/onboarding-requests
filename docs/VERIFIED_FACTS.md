# Verified Facts

Every free-tier limit, runtime and platform behaviour this pilot depends on. Nothing here is assumed true until its **Status** says so.

**Phase 0 note (16 Oct 2026):** `learn.microsoft.com` is blocked by the build environment's network policy. Facts below were taken from web-search extracts of the Microsoft Learn pages, not from a full page read. Status `SEARCH` means "seen in search extract, must be re-read on the live page before Phase 6". Status `CONFLICT` means it differs from the pilot prompt and needs a decision.

| # | Fact | Value found | Source URL | Checked | Status |
|---|---|---|---|---|---|
| F1 | SWA managed Functions trigger types | HTTP triggers and bindings only. No Durable Functions. | https://learn.microsoft.com/azure/static-web-apps/apis-functions | 2026-10-16 | SEARCH |
| F2 | SWA managed Functions managed identity | Not supported. Key Vault references not supported. | https://learn.microsoft.com/azure/static-web-apps/apis-functions | 2026-10-16 | SEARCH |
| F3 | SWA API request timeout (all backends) | **45 seconds** max per API request. Gateway returns 500; function may keep running. | https://learn.microsoft.com/azure/static-web-apps/apis-overview | 2026-10-16 | SEARCH — conflict resolved by Q3: processing budget 30 s (`PROCESSING_BUDGET_SECONDS`) |
| F4 | SWA managed Functions .NET runtime | Current docs table lists **.NET 10.0 isolated, `dotnet-isolated:10.0`** (no end date); .NET 8/9 isolated end 10 Nov 2026. Older Q&A posts (pre-Oct 2026) said 10.0 was not yet supported; an Oct 2026 GitHub comment says it now is. **Decision Q1 = A: .NET 10 isolated.** Confirm on first deploy. | https://learn.microsoft.com/azure/static-web-apps/languages-runtimes , https://github.com/Azure/static-web-apps-cli/issues/1002 | 2026-10-16 | SEARCH — re-read page; prove with the Phase 6 deploy |
| F5 | Azure Functions in-process .NET model | End of support **10 Nov 2026**. | https://azure.microsoft.com/updates/retirement-support-for-the-inprocess-model-for-net-apps-in-azure-functions-ends-10-november-2026 | 2026-10-16 | SEARCH |
| F6 | .NET 8 end of support | **10 Nov 2026** (25 days from today). .NET 10 LTS supported to 14 Nov 2028. | https://learn.microsoft.com/azure/azure-functions/migrate-dotnet-to-isolated-model | 2026-10-16 | SEARCH — resolved by Q1 = A (.NET 10) |
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
| F19 | Cost Management budget currency | Budget amount is in the billing account currency. ₹1 only works if billing currency is INR. | https://learn.microsoft.com/azure/cost-management-billing/costs/tutorial-acm-create-budgets ; live `az consumption budget list` | 2026-10-16 | **VERIFIED live** — deployed budget reports unit INR |

| F20 | SWA config: whole-site sign-in | `routes: [{route:"/*", allowedRoles:["authenticated"]}]` + `responseOverrides.401` redirect to `/.auth/login/aad`; unused providers blocked with `statusCode: 404` routes. Works on Free. | https://learn.microsoft.com/azure/static-web-apps/configuration | — | **UNVERIFIED** (prior knowledge; re-read) |
| F21 | SWA CLI config schema vs `dotnet-isolated:10.0` | **SWA CLI 2.0.10 (latest on npm, 16 Oct 2026) rejects `apiRuntime: dotnet-isolated:10.0`**; its schema lists `dotnet:8.0`, `dotnet-isolated:8.0`, `dotnet-isolated:9.0`, `node:18/20/22`, `python:3.9-3.11`. The emulator then ignores the whole config file. Open CLI issue to add 10.0. | Observed locally: `npx swa start` output; https://github.com/Azure/static-web-apps-cli/issues/1002 | 2026-10-16 | **CONFIRMED in Azure deploy (16 Oct 2026)**: `swa deploy` with `--api-language dotnet-isolated --api-version 10.0` fails; the deploy client (StaticSitesClient build 134664351b) reports "Function language info isn't provided". Gokul's Cloud Shell check: the deploy client binary *does* contain the string `dotnet-isolated:10.0`, so the likelier cause is the CLI rejecting the config file schema and dropping it before upload (not proven). **Decision A (16 Oct 2026): API on `dotnet-isolated:9.0`** (passes CLI schema); libraries multi-target net9.0/net10.0. Retry 10.0 when the CLI schema accepts it (issue #1002) — two-line switch guarded by `InfraConsistencyTests`. **Update:** redeploy on 9.0 failed the same way, so the version was not the cause; the deploy client needs `staticwebapp.config.json` inside the uploaded app folder (`dist`). .NET 10 may therefore work once that is fixed — to be re-tested after a successful 9.0 deploy. |
| F22 | Cosmos periodic backup cost | Two backup copies are understood to be free; default backup storage redundancy is Geo. | https://learn.microsoft.com/azure/cosmos-db/periodic-backup-restore-introduction | — | **UNVERIFIED** — check before deploy (docs/COSTS.md watch list) |
| F23 | Azure Policy assignment effect delay | New assignments can take up to ~30 min to start evaluating. | https://learn.microsoft.com/azure/governance/policy/overview | — | **UNVERIFIED** (prior knowledge) |
| F24 | Allowed region list for SWA (Free + managed Functions) | Bicep restricts `swaLocation` to centralus, eastus2, eastasia, westeurope, westus2. | https://learn.microsoft.com/azure/static-web-apps/overview | — | **UNVERIFIED** — confirm with Q6 region answer |

### Build-environment facts (not platform limits)
- `learn.microsoft.com`, `aka.ms`, `dot.net`, `builds.dotnet.microsoft.com`, the Functions Core Tools CDN and GitHub releases were blocked here. .NET 10 SDK came from Ubuntu packages; Bicep 0.48.1 from the official `Azure.Bicep.CommandLine.linux-x64` NuGet package. Functions Core Tools was not obtainable, so local E2E used `tools/local-api` (same `ApiHost`) behind the SWA CLI emulator.

### SWA auth: Free vs Standard (as used by this pilot)
| Feature | Free | Standard | Pilot uses |
|---|---|---|---|
| Pre-configured Entra ID provider | Yes | Yes | Yes |
| Route `allowedRoles` (`authenticated`) | Yes | Yes | Yes |
| Custom roles via invitations | Yes (F9 extract) | Yes | No |
| Custom roles via serverless function | No (F9) | Yes | No — roles live in Cosmos `userAccess` |
| Custom Entra registration (tenant restriction) | Unverified (F11) | Yes | No (Q2 accepted) |

## Re-verification rule
Before Phase 6 what-if, every row must be `VERIFIED` (live page read, date recorded) or explicitly accepted by Gokul.
