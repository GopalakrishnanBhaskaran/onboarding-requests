# Enterprise handoff: onboarding flow to the real Azure tenants (PEST and Vestacy)

Updated 22 Oct 2026 (evening).
Code: branch `claude/great-archimedes-cjr4rl`. It includes all of v3 up to commit `fd5491c` from the session "Onboarding v3 test notifications and edge cases".
Pilot go-live steps (the pilot tenant, 23 Oct): `docs/GO_LIVE.md`. This file covers the move to the real tenants.


## 1. Where it stands

Done in the pilot:
- The live end-to-end test reached **Completed** on 22 Oct (request 34456531, PEST, Dev, Support Reader). Live check R: 20 PASS, 1 WAIT. The WAIT is only the go-live switch to prod.
- Live in the pilot: agent provisioning, agent TAP mail, PEST and Vestacy imported, SAMPLE data removed.
- v3 deployed (`1f01fda`):
  - the completion mail goes to the requester, not the Owner;
  - completion no longer waits for the joiner's sign-in;
  - a Contact inventory page;
  - a mail queue within the mail limit.
- Tests: .NET 380/381 pass (1 opt-in skip), acceptance 27/27, Playwright 8/8, SPA 10/10.

Still to do in the pilot:
- Redeploy the website (`deploy_free.sh app`) for the table-layout fix `fd5491c`.
- Follow `docs/GO_LIVE.md`: real contacts, then `environment prod`.

Not ready yet for the real tenants: the tools are built for one free-tier pilot. Section 2 lists the gaps.


## 2. Gaps to close before the real deploy (code work, prompt A)

G1. Names are hardcoded.
`rg-pmo-review-pilot` and the resource names are fixed in deploy_free.sh, verify_features.sh, workflow_diag.sh, cost_check.sh and check_free_tier.sh.
Fix: parameters for tenant, environment, resource group and name prefix, with one params file per tenant and environment.

G2. Anyone with a Microsoft account can sign in (F10).
Fix: Static Web Apps **Standard** with your own Entra app registration (single tenant, "assignment required").

G3. The database key sits in the website settings, and the API runs on .NET 9 (support ends **10 Nov 2026**).
Fix: move the API to its own Function App on .NET 10 with a managed identity. Turn off Cosmos keys (`disableLocalAuth`).

G4. The free-tier setup does not fit an enterprise subscription.
Fix: Cosmos serverless or provisioned with an RU cap; the org's budget; Bicep that passes the org's Azure Policy (tags, regions, public network, diagnostics, storage keys off).

G5. Mail limit.
The Azure-managed mail domain sends at most **10 mails an hour**, and one request needs about 7 (F70).
Fix: ACS with a **custom verified domain** (DNS records), then raise `MAIL_MAX_PER_MINUTE` / `MAIL_MAX_PER_HOUR`. Or use Graph mail from a shared mailbox.

G6. Test-only switches must not reach prod: `--test-scope`, `--test-contact`, `NOTIFY_REDIRECT_TO`.
Fix:
- import with `--resolve` or `--scope-map` and the real tenant id;
- fill the Contact inventory page with real team addresses;
- run with `APP_ENVIRONMENT=prod`.

G7. Deploys are run by hand from Cloud Shell.
Fix: an Azure DevOps pipeline per tenant and environment, with workload identity federation and approval checks.

G8. Logs go to the console only.
Fix: App Insights and Log Analytics with a daily cap.

G9. Least privilege.
- `AuditLog.Read.All` is no longer used (there is no sign-in check). Do not grant it in the real tenants.
- Privileged rows (`privileged … on`) stay off until Security decides which rows the agent may do.


## 3. Target design

One stack per tenant:
- PEST in a subscription of the PEST tenant;
- Vestacy in a subscription of the Vestacy tenant.

Each worker identity lives in its own tenant, so `AGENT_TENANT_ID` is that tenant and the agent can create accounts there. Cross-tenant provisioning is not built, and this design does not need it.

Each stack, for test and then prod (each resource needs written cost approval):
- Static Web Apps Standard
- API Function App (Flex Consumption, .NET 10, managed identity)
- worker Function App (Flex Consumption, managed identity) and its storage account
- Cosmos DB for NoSQL
- Azure Communication Services and Email Service (custom domain)
- Key Vault (the sign-in app secret)
- App Insights and Log Analytics
- a budget


## 4. Tools for the real environment

To run the steps:
- Azure CLI (latest) and Bicep (`az bicep install`): what-if, deploy, role grants.
- Azure DevOps Pipelines (`pipelines/azure-pipelines.yml`), service connection with workload identity federation, Environments with approval checks, Key Vault-linked variable group.
- .NET 10 SDK: API and worker build and tests.
- Node.js 22 LTS and npm (the SWA CLI or the `AzureStaticWebApp@0` task): website build and deploy.
- Python 3.11+ with `tools/requirements.txt`: import and checks.
- git, bash, zip, curl.
- Azure Portal, Entra admin center, PIM: admin consent, app registration, TAP policy, role activation.

Repo tools (after G1 is done):
- `deploy_free.sh` (to become `deploy.sh`): deploy, permissions, environment switch.
- `import_workbook.py`: imports a tenant's workbooks.
- `register_user.py`: gives people roles by sign-in name (`--list`, `--remove`).
- `sheet_roles.py` and `provisioning … --from-sheet`: build the role allow-list from the mapping sheet.
- `verify_features.sh`: PASS / WAIT / FAIL checklist. `workflow_diag.sh`: where a request is stuck.
- `cost_check.sh`, `run_acceptance.sh`, `mac_setup.sh` (deploy from a Mac).

Do not use in the real tenants: `seed_sample_data.py` (SAMPLE data), `--test-scope`, `--test-contact`, `--redirect`.


## 5. Who does what (per tenant)

- **Sponsor (Gokul):** cost and scope approval, go / no-go.
- **Security approver:** approves sign-in (G2), identity (G3), every Graph permission, and provisioning, TAP and privileged rows "on".
- **Subscription Owner or User Access Administrator:**
  - creates the subscription and resource group;
  - gives the pipeline identity Contributor and constrained RBAC Administrator on the resource group;
  - gives the worker constrained RBAC Administrator on the real target scopes.
- **Privileged Role Administrator or Global Administrator (with PIM):** grants the worker's Graph app roles.
  - Read: `User.Read.All`, `GroupMember.Read.All`, `UserAuthenticationMethod.Read.All`.
  - Create: `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All`.
  - TAP: `UserAuthMethod-TAP.ReadWrite.All`.
- **Application Administrator:** sign-in app registration, "assignment required", assigns the user groups.
- **Authentication Policy Administrator:** TAP policy: on, one-time, maximum lifetime of at least `AGENT_TAP_LIFETIME_MINUTES`, target group.
- **DNS admin (or Exchange admin for Graph mail):** mail domain records.
- **DevOps / platform engineer:** pipeline, what-if, deploy, checks, cost check.
- **PMO:**
  - supplies the classification and response workbooks;
  - owns the mapping sheet;
  - fills the **Contact inventory** per tenant.
- **UAT testers:** Requester, named Owner, Security approver (a different person), ID team, role assignment team.
- **FinOps:** budget and alerts.


## 6. Steps in order (PEST first, then the same for Vestacy)

1. **Pilot go-live** (`docs/GO_LIVE.md`) and the `app` redeploy for `fd5491c`.
   Who: Gokul.
2. **Bring the code into the org:** `git bundle create pmo.bundle claude/great-archimedes-cjr4rl`, then import it into the org repo. Never commit the workbooks.
   Who: DevOps. Gate: the org's code-intake rules.
3. **Close gaps G1–G9** with enterprise Claude Code (prompt A). All tests and `bicep build` green.
   Who: developer with Claude. Gate: PR review.
4. **Written approvals:**
   - cost: the resource list from step 6;
   - security: G2, G3, permissions, privileged rows;
   - data: the workbook import.
   Who: Sponsor, Security, PMO.
5. **Tenant prerequisites:** subscription and resource group, pipeline connection, sign-in app registration, TAP policy, mail domain DNS.
   Who: Subscription Owner, App Admin, Authentication Policy Admin, DNS admin.
6. **What-if for pest-test**, then review the resources and cost.
   Who: DevOps, then Sponsor. Gate: "approved".
7. **Deploy pest-test** (infra, API, website, worker) through the pipeline.
   Who: DevOps. Gate: environment check.
8. **Graph read roles** for the worker.
   Who: Privileged Role Admin. Gate: Security.
9. **Import PEST** with `import_workbook.py --tenant PEST --resolve --tenant-id <PEST tenant id>`. Then give people their roles with `register_user.py --project PEST --user <sign-in name> --roles …`.
   Who: platform engineer with PMO. Gate: data owner.
10. **Contact inventory** for PEST in the app (Security, ID team, role assignment team, PMO, Operators, escalation managers).
    Who: PMO.
11. **Test mode with mail:** `environment test --redirect <UAT mailbox>`.
    Who: platform engineer. Gate: mail approval.
12. **Provisioning on** with `--from-sheet PEST` on the real scopes (dry run, then `--approved-by`).
    Who: Subscription Owner and Privileged Role Admin. Gate: Security.
13. **Agent TAP on** (the dry run shows the TAP policy, then `--approved-by`). A new permission can take **up to 24 h** to reach the worker (F61).
    Who: Privileged Role Admin. Gate: Security.
14. **Checks:** `verify_features.sh`, every line PASS except 15 (go-live).
    Who: platform engineer.
15. **UAT end-to-end:**
    - request → AI review → Owner → Security;
    - the agent creates the user, group and membership and mails the TAP;
    - the agent assigns the roles → **Completed**;
    - the completion mail goes to the requester, not the Owner;
    - a red or Prod row is returned.
    Who: UAT testers. Gate: test sign-off.
16. **pest-prod:** repeat steps 6–14, then `environment prod` (no redirect). Line 15 must be PASS. Send the go-live mail (template in `docs/GO_LIVE.md`).
    Who: DevOps and admins. Gate: go / no-go.
17. **Hypercare:**
    - run `workflow_diag.sh` daily;
    - cost check after 48 h;
    - watch "mail waiting" issues and High "mail reached nobody" issues.
    Rollback: `provisioning off` or `tap off` (always allowed), `environment test --redirect …`, or stop the worker.
    Who: platform engineer.
18. **Vestacy:** repeat steps 5–17 in the Vestacy tenant (169 rows).
    Who: the same roles in the Vestacy tenant. Gate: the same gates.


## 7. Rules that do not change

- The agent never approves and never extends its own rights. Teams can hand tasks back to the agent; it re-checks.
- Every security change: dry run → written approval → `--approved-by`.
- Never paste or log a TAP, password, connection string or personal user id.
- Real workbook data never goes into git.
