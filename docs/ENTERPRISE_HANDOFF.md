# Enterprise handoff — PMO onboarding flow v2.2 → real Azure (PEST + Vestacy)

Written 22 Oct 2026 from the session *Onboarding v2 end-to-end test – 21 Oct – Michael Plaumann review comments* (branch `claude/great-goodall-j4ozev`, last commit `058306a`). The review comments are taken as the session built them: `SCOPE.md` "21 Oct" and "22 Oct" additions. If Michael gave more comments than those, add them to the prompt in `docs/ENTERPRISE_CLAUDE_PROMPTS.md`.

## 1. Where it stands (facts)

| Item | Status |
|---|---|
| Code v2.2 | Built and tested locally: .NET 331/332 pass (1 opt-in skip), acceptance 27/27, Playwright 7/7, SPA 8/8 |
| Pilot (personal tenant, sub `5b9f…8570`) | v2.1 + mail live. **The E2E has not reached Completed yet.** Blocked at step 6 of `docs/NEXT_SESSION.md` (`deploy_free.sh` bug fixed in `058306a`: re-run it) |
| Agent TAP, dropdown form, real PEST/Vestacy import | Code only, not deployed |
| Enterprise readiness | **Not ready as is.** The tools target a free-tier pilot (gaps in section 2) |

**Recommendation:** finish the pilot E2E (NEXT_SESSION steps 6–10) before you start in the enterprise. It is the only proof that the whole flow works.

## 2. Gaps to close before the enterprise deploy (code; done by enterprise Claude Code)

| # | Gap | Why | Fix |
|---|---|---|---|
| G1 | RG, names and deployment are hardcoded (`rg-pmo-review-pilot`) in `deploy_free.sh`, `verify_features.sh`, `workflow_diag.sh`, `cost_check.sh`, `check_free_tier.sh` | You need one stack per tenant and per environment | `--tenant`/`--env`/`--rg`/`--prefix` parameters; one params file per tenant + env |
| G2 | SWA Free + the pre-configured Entra provider lets **any Microsoft account** sign in (F10) | Security will not accept this | SWA **Standard** + custom Entra registration (single tenant, *assignment required*), PROMOTION swap 5 |
| G3 | Cosmos connection string sits in SWA app settings. SWA managed API has no managed identity (F2), has a 45 s limit (F3) and runs on .NET 9, which goes out of support on **10 Nov 2026** (F21) | Key-based access; runtime going end of life | BYO Function App on .NET 10 with managed identity; Cosmos `disableLocalAuth: true` (swap 2) |
| G4 | Cosmos free tier, ₹2,000 budget, allowed-types policy | The enterprise sub may already use its free-tier slot, and the org's own policies apply | Serverless or provisioned Cosmos with an RU cap; the org budget; align Bicep with org policy (tags, regions, public network, diagnostics, storage shared-key off) |
| G5 | Mail goes out from the Azure-managed `DoNotReply` domain (low limits, unbranded) | Not fit for production | ACS with a **custom verified domain** (SPF/DKIM), or Graph `sendMail` from a shared mailbox limited by Exchange RBAC for Applications |
| G6 | Test-only switches: `--test-scope`, `--test-contact`, `NOTIFY_REDIRECT_TO` | Real mail would go to one tester | Prod path: `--resolve`/`--scope-map`, real contacts, `APP_ENVIRONMENT=prod` (the redirect is refused there) |
| G7 | Deploys run from Cloud Shell by hand | No audit or approvals | `pipelines/azure-pipelines.yml` per tenant + env, workload identity federation, approval checks on environments |
| G8 | Logs go to the console only | Prod needs monitoring | App Insights + Log Analytics with a daily cap (swap 4; needs approval) |

## 3. Target design (per tenant)

**One stack per tenant.** PEST runs in a PEST-tenant subscription and Vestacy in a Vestacy-tenant subscription. Each worker identity lives in its own tenant (`AGENT_TENANT_ID`), so the agent can provision there. Cross-tenant provisioning is **not built**, and this design avoids it (CLAUDE.md rule 10).

Per tenant and environment (`test`, then `prod`), each resource needs written cost approval:
SWA Standard · API Function App (Flex, .NET 10, MI) · worker Function App (Flex, MI) + its storage · Cosmos NoSQL · ACS + Email Service (custom domain) · Key Vault (SWA auth secret) · App Insights + Log Analytics · budget.

## 4. Tools to use in the real environment

| Tool | Used for | Who |
|---|---|---|
| Azure CLI (latest) + Bicep (`az bicep install`) | what-if, deploy, role grants | Platform engineer, pipeline |
| Azure DevOps Pipelines + service connection (workload identity federation) + Environments with approval checks + Key Vault-linked variable group | CI/CD per tenant/env | DevOps engineer |
| .NET 10 SDK (keep .NET 9 only while the API is still on SWA managed functions) | build/test API + worker | Pipeline, developer |
| Node.js 22 LTS + npm, SWA CLI or the `AzureStaticWebApp@0` task | SPA build/deploy | Pipeline |
| Python 3.11+ with `tools/requirements.txt` (azure-cosmos, openpyxl, jsonschema) | `import_workbook.py`, `verify_features.py` | Platform engineer + PMO |
| git, bash, zip, curl | scripts | All |
| Azure Portal, Entra admin center, PIM | admin consent, app registration, TAP policy, role activation | Entra / subscription admins |
| Repo tools (after G1): `deploy*.sh`, `import_workbook.py`, `verify_features.sh`, `workflow_diag.sh`, `cost_check.sh`, `run_acceptance.sh` | deploy, data import, checks | Platform engineer |
| Optional: Functions Core Tools v4, Docker (Cosmos emulator test), Playwright | local tests, UAT automation | Developer |
| **Do not use** in the enterprise: `seed_sample_data.py` (SAMPLE data) and `--test-*` flags in prod | — | — |

## 5. Who does what (per tenant)

| Role (Entra / Azure) | Steps they own |
|---|---|
| **Sponsor / approver** (Gokul) | Cost and scope approval, go/no-go |
| **Security approver** (CISO delegate) | Approves G2/G3, each Graph permission, provisioning **on**, TAP **on** (`--approved-by`) |
| **Subscription Owner / User Access Administrator** | Creates sub/RG; gives the pipeline identity Contributor + constrained RBAC Administrator on the RG; constrained RBAC Administrator for the worker MI on the **real target scopes** |
| **Privileged Role Administrator or Global Administrator** (via PIM) | Grants Graph app roles to the worker MI: read `User.Read.All`, `GroupMember.Read.All`, `UserAuthenticationMethod.Read.All`, `AuditLog.Read.All`; write `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All`; TAP `UserAuthMethod-TAP.ReadWrite.All` |
| **Application Administrator / Cloud Application Administrator** | SWA app registration (single tenant), *assignment required*, assigns the user groups |
| **Authentication Policy Administrator** | TAP policy: enabled, one-time, max lifetime ≥ `AGENT_TAP_LIFETIME_MINUTES`, target group |
| **DNS admin** (or Exchange admin if Graph mail) | Custom mail domain records (or the Exchange application access scope) |
| **DevOps / platform engineer** | Pipelines, what-if, deploy, `verify_features`, cost check |
| **PMO (data owner)** | Supplies the classification + response workbooks; owns the mapping sheet |
| **UAT testers** | Requester, named Owner, Security approver (not the same person), ID team, role team |
| **FinOps** | Budget + alerts |

## 6. Steps in order (PEST first, then repeat for Vestacy)

| # | Step | Who | Gate |
|---|---|---|---|
| 0 | Finish the pilot E2E to Completed (`NEXT_SESSION.md` steps 6–10) | Gokul | — |
| 1 | Bring the repo into the org: `git bundle create pmo.bundle claude/great-archimedes-cjr4rl`, then import it into the org repo (ADO Repos / GHE). **Never commit the workbooks** (`*.xlsx` is ignored) | DevOps | Org code-intake policy |
| 2 | Close G1–G8 with enterprise Claude Code (prompt A); all tests and `bicep build` green | Developer + Claude | PR review |
| 3 | Approvals in writing: cost (resource list from step 5), security (G2, G3, permissions), data (workbook import) | Sponsor, Security, PMO | **Written approval** |
| 4 | Tenant prerequisites: sub + RG; WIF service connection; SWA app registration; TAP policy; mail domain DNS | Sub Owner, App Admin, Auth Policy Admin, DNS admin | — |
| 5 | Pipeline **what-if** for `pest-test` → review the resource list and cost | DevOps → Sponsor | "approved" |
| 6 | Pipeline deploy `pest-test` (infra + API + SPA + worker) | DevOps (pipeline) | Environment check |
| 7 | Graph **read** roles to the worker MI | Privileged Role Admin | Security |
| 8 | Import PEST: `import_workbook.py --tenant PEST --resolve --tenant-id <PEST tenant>` (real scopes), then register the UAT users' roles | Platform engineer + PMO | Data owner |
| 9 | `environment test --redirect <UAT mailbox>`; mail switched on | Platform engineer | Mail approval |
| 10 | Provisioning **on** with the real allow-listed roles and scopes (dry run → `--approved-by`) | Sub Owner + Privileged Role Admin | Security |
| 11 | Agent TAP **on** (dry run shows the tenant TAP policy → `--approved-by`). Allow **up to 24 h** for the permission to reach the worker (F61) | Privileged Role Admin | Security |
| 12 | `verify_features.sh` → every row PASS | Platform engineer | — |
| 13 | UAT E2E: request → AI review → Owner → Security → agent creates user/group/membership + mails the TAP → role assignment → joiner signs in + Authenticator → **Completed**. Also test that a red/Prod row is returned | UAT testers | Test sign-off |
| 14 | `pest-prod`: repeat 5–12 with `environment prod` (no redirect) and real contacts | DevOps + admins | Go/no-go |
| 15 | Hypercare: `workflow_diag.sh` daily, cost check at +48 h, SLA reminders. Rollback: `provisioning off` / `tap off` (always allowed), stop the worker | Platform engineer | — |
| 16 | **Vestacy**: repeat 4–15 in the Vestacy tenant (169 rows) | Same roles, Vestacy tenant | Same gates |

## 7. Rules that carry over (unchanged)
The agent never approves. Each security change follows dry run → written approval → `--approved-by`. Never paste or log a TAP, password, connection string or personal user id. Real workbook data never goes into git.
