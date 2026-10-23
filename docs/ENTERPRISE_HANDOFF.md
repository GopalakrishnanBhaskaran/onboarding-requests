# Enterprise handoff: onboarding flow to the real Azure tenants (PEST and Vestacy)

Updated 23 Oct 2026: **private endpoint only** (agreed), App Service hosting, encrypted sign-in mail.
Code: branch `claude/great-archimedes-cjr4rl`. It includes all of v3 up to commit `fd5491c` from the session "Onboarding v3 test notifications and edge cases".

Related docs:
- Target architecture: `docs/ARCHITECTURE.md` section 12.
- Settings and *(verify)* items: `docs/SECURE_HOSTING_AND_MAIL.md`.
- Pilot go-live: `docs/GO_LIVE.md`.
- Fast path with the scripts as they are: `docs/GO_LIVE_ACTUAL_TENANT.md`. It deploys a public site, so it is not the agreed design.


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

**Not ready yet for the real tenants.** The tools are built for one public, free-tier pilot. Section 2 lists the gaps; prompt A closes them (tasks A1–A12).


## 2. Gaps to close before the real deploy (code work, prompt A)

G1. **Names are hardcoded.**
`rg-pmo-review-pilot` and the resource names are fixed in deploy_free.sh, verify_features.sh, workflow_diag.sh, cost_check.sh and check_free_tier.sh.
Fix: parameters for tenant, environment, resource group and name prefix, with one params file per tenant and environment. (A1)

G2. **Anyone with a Microsoft account can sign in** (F10).
Fix: App Service sign-in with Entra (single tenant, "assignment required"), and Conditional Access (MFA, compliant device). The API rejects a `tid` other than the tenant's. (A10)

G3. **Static Web Apps hosting.** The database key sits in the website settings, and the API runs on .NET 9 (support ends **10 Nov 2026**).
Fix: **one App Service app** (Linux, .NET 10) serving the website and the API (`src/webhost`), with a managed identity to Cosmos and Cosmos keys off (`disableLocalAuth`). (A3, A10)

G4. **The free-tier setup does not fit an enterprise subscription.**
Fix: Cosmos serverless or provisioned with an RU cap; the org's budget; Bicep that passes the org's Azure Policy (tags, regions, diagnostics, storage keys off). (A4, A6)

G5. **Mail limit.**
The Azure-managed mail domain sends at most **10 mails an hour**, and one request needs about 7 (F70).
Fix: ACS with a **custom verified domain**, then raise `MAIL_MAX_PER_MINUTE` / `MAIL_MAX_PER_HOUR`. (A5)

G6. **Test-only switches must not reach prod:** `--test-scope`, `--test-contact`, `NOTIFY_REDIRECT_TO`.
Fix:
- import with `--resolve` or `--scope-map` and the real tenant id;
- the Contact inventory page with real team addresses;
- `APP_ENVIRONMENT=prod`.

G7. **Deploys are run by hand from Cloud Shell.**
Fix: an Azure DevOps pipeline per tenant and environment, with workload identity federation, approval checks, and a **self-hosted agent pool in the VNet**. (A8, A12)

G8. **Logs go to the console only.**
Fix: App Insights and Log Analytics with a daily cap, through an Azure Monitor Private Link Scope if the org requires one. (A7)

G9. **Least privilege.**
- `AuditLog.Read.All` is no longer used: do not grant it.
- Privileged rows (`privileged … on`) stay off until Security decides. (A5b)

G10. **Public access.** Today everything has a public endpoint.
Fix: **private endpoint only**:
- public network access off on the app (and its deployment site), Cosmos, Key Vault and the worker's storage;
- worker inbound off, with VNet integration;
- private DNS zones;
- an outbound firewall allow-list;
- the admin tools sign in to Cosmos with Entra, not a key, and run inside the VNet. (A12)

G11. **The password and TAP mail is not encrypted** (TLS in transit only).
Fix: send it through Graph from one shared mailbox with the header `x-pmo-sensitivity: signin`. An Exchange rule applies Purview Message Encryption. `REQUIRE_ENCRYPTED_SIGNIN_MAIL` is on in prod. Optional: do not mail the password. (A11)


## 3. Target design (per tenant: see `docs/ARCHITECTURE.md` section 12)

One stack per tenant:
- PEST in a subscription of the PEST tenant;
- Vestacy in a subscription of the Vestacy tenant.

`AGENT_TENANT_ID` is that tenant, so the agent can create accounts there. Cross-tenant provisioning is not built, and this design does not need it.
Users reach the app **only from the corporate network or VPN**, at a private IP.

Each stack, for test and then prod (each resource needs written cost approval):
- **App Service plan and app** (Linux, .NET 10): the website and API, Entra sign-in, HTTPS only, TLS 1.2+, public access off.
- **Worker Function App** (Flex Consumption, managed identity, VNet integration, inbound off) and its storage account.
- **Cosmos DB for NoSQL**: keys off, public access off.
- **Key Vault**: the sign-in app secret and any certificate.
- **Private endpoints**: app, Cosmos, Key Vault, storage (blob, queue, table).
- **Private DNS zones**, linked to the hub:
  - `privatelink.azurewebsites.net`;
  - `privatelink.documents.azure.com`;
  - `privatelink.vaultcore.azure.net`;
  - `privatelink.blob/queue/table.core.windows.net`.
- **Spoke VNet** peered to the hub, with subnets for the private endpoints and for worker integration.
- **Azure Communication Services and Email Service** with a custom domain, for normal mail.
- **Exchange Online shared mailbox** and a mail flow rule, for the encrypted password and TAP mail (no Azure cost).
- **App Insights and Log Analytics.**
- **Self-hosted build agent VM** (or a jump box with Bastion) in the VNet.
- Optional: **Application Gateway WAF v2** with a private front end, if Security asks for a WAF.
- A budget.


## 4. Tools for the real environment

To run the steps (all deploys and admin tools run **inside the VNet**):
- **Azure DevOps Pipelines** (`pipelines/azure-pipelines.yml`):
  - a **self-hosted agent pool in the VNet**;
  - a service connection with workload identity federation;
  - Environments with approval checks;
  - a Key Vault-linked variable group.
- Azure CLI (latest) and Bicep (`az bicep install`): what-if, deploy, role grants.
- .NET 10 SDK: build and test the web host, API and worker.
- Node.js 22 LTS and npm: website build (copied into the web host's `wwwroot`).
- App deploy: the `AzureWebApp@1` and `AzureFunctionApp@2` tasks, from the self-hosted agent (the deployment sites are private).
- Python 3.11+ with `tools/requirements.txt`: import and checks, signed in with Entra.
- git, bash, zip, curl, nslookup.
- Azure Portal, Entra admin center, PIM: admin consent, app registration, TAP policy, Conditional Access, role activation.
- Exchange admin center and Exchange Online PowerShell: shared mailbox, RBAC for Applications, mail flow rule.
- Microsoft Purview portal: message encryption templates.

Repo tools (after G1 and G10 are done):
- `deploy.sh` (was `deploy_free.sh`): deploy, permissions, environment switch.
- `import_workbook.py`: imports a tenant's workbooks.
- `register_user.py`: gives people roles by sign-in name (`--list`, `--remove`).
- `sheet_roles.py` and `provisioning … --from-sheet`: build the role allow-list from the mapping sheet.
- `verify_features.sh`: PASS / WAIT / FAIL checklist. `workflow_diag.sh`: where a request is stuck.
- `cost_check.sh`, `run_acceptance.sh`.

Do not use in the real tenants:
- `seed_sample_data.py` (SAMPLE data);
- `--test-scope`, `--test-contact`, `--redirect`;
- Cloud Shell outside the VNet;
- Cosmos keys.


## 5. Who does what (per tenant)

- **Sponsor (Gokul):** cost and scope approval, go / no-go.
- **Security approver:**
  - approves the design (private access, sign-in, encrypted mail, password or no password);
  - approves every Graph permission, including `Mail.Send`;
  - approves provisioning, TAP and privileged rows "on".
- **Network admin:**
  - spoke VNet and peering;
  - private endpoints and private DNS zones;
  - the DNS Private Resolver and the on-premises forwarders;
  - the firewall outbound allow-list (Entra, Graph, `management.azure.com`, ACS, monitoring);
  - optional private Application Gateway WAF.
- **Subscription Owner or User Access Administrator:**
  - creates the subscription and resource group;
  - gives the pipeline identity Contributor and constrained RBAC Administrator on the resource group;
  - gives the worker constrained RBAC Administrator on the real target scopes.
- **Privileged Role Administrator or Global Administrator (with PIM):** grants the worker's Graph app roles.
  - Read: `User.Read.All`, `GroupMember.Read.All`, `UserAuthenticationMethod.Read.All`.
  - Create: `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All`.
  - TAP: `UserAuthMethod-TAP.ReadWrite.All`.
  - Mail: `Mail.Send`.
- **Application Administrator:** the sign-in app registration, "assignment required", assigns the user groups.
- **Conditional Access Administrator:** the MFA and compliant-device policy for the app.
- **Authentication Policy Administrator:** TAP policy: on, one-time, maximum lifetime of at least `AGENT_TAP_LIFETIME_MINUTES`, target group.
- **Exchange Administrator:**
  - the shared mailbox `onboarding-signin@<domain>`;
  - Exchange RBAC for Applications, so `Mail.Send` works only for that mailbox;
  - the mail flow rule that encrypts mail with `x-pmo-sensitivity: signin`.
- **Compliance Administrator:** Purview Message Encryption templates (Encrypt-Only or Do Not Forward) and branding.
- **DNS admin:** the records for the custom mail domain.
- **DevOps / platform engineer:**
  - the self-hosted agent (or jump box) in the VNet;
  - the pipeline, what-if, deploy, checks and cost check.
- **PMO:**
  - supplies the classification and response workbooks;
  - owns the mapping sheet;
  - fills the **Contact inventory** per tenant.
- **UAT testers:** Requester, named Owner, Security approver (a different person), ID team, role assignment team. All on the corporate network or VPN.
- **FinOps:** budget and alerts.


## 6. Steps in order (PEST first, then the same for Vestacy)

1. **Pilot go-live** (`docs/GO_LIVE.md`) and the `app` redeploy for `fd5491c`.
   Who: Gokul.

2. **Bring the code into the org:** `git bundle create pmo.bundle claude/great-archimedes-cjr4rl`, then import it into the org repo. Never commit the workbooks.
   Who: DevOps. Gate: the org's code-intake rules.

3. **Close gaps G1–G11** with enterprise Claude Code (prompt A, tasks A1–A12). All tests and `bicep build` green.
   Who: developer with Claude. Gate: PR review.

4. **Written approvals:**
   - cost: the resource list in section 3;
   - security: the private-only design, sign-in, encrypted mail, password or no password, permissions, privileged rows;
   - data: the workbook import.
   Who: Sponsor, Security, PMO.

5. **Network prerequisites:**
   - spoke VNet and peering;
   - private DNS zones linked to the hub;
   - on-premises DNS forwarders to the Private Resolver;
   - the firewall outbound allow-list.
   Who: network admin.

6. **Build agent:** a self-hosted Azure DevOps agent (or a jump box with Bastion) in the VNet, registered in the agent pool.
   Who: DevOps.

7. **Tenant prerequisites:**
   - subscription and resource group, and the pipeline connection;
   - the sign-in app registration ("assignment required") and the Conditional Access policy;
   - the TAP policy;
   - the custom mail domain DNS.
   Who: Subscription Owner, App Admin, Conditional Access Admin, Authentication Policy Admin, DNS admin.

8. **Encrypted mail setup:**
   - the shared mailbox;
   - Exchange RBAC for Applications for the worker identity on that mailbox only (after step 10 creates the identity);
   - the mail flow rule for `x-pmo-sensitivity: signin`, with Purview encryption;
   - a test with one mail to a test mailbox inside and outside the tenant.
   Who: Exchange Admin, Compliance Admin.

9. **What-if for pest-test** from the pipeline. Check that every resource has public access off and a private endpoint, then review the resources and cost.
   Who: DevOps, then Sponsor. Gate: "approved".

10. **Deploy pest-test** (infra, web host, worker) through the pipeline on the self-hosted agent.
    Who: DevOps. Gate: environment check.

11. **Private access check:**
    - from a corporate laptop, `nslookup <app>.azurewebsites.net` returns a **private IP**, and the site opens with Entra sign-in;
    - from outside the network, the site **does not answer**;
    - an account from another tenant is refused;
    - Cosmos and Key Vault refuse public access.
    Who: network admin and DevOps.

12. **Graph roles for the worker:** read roles, then `Mail.Send` (then finish the Exchange RBAC part of step 8).
    Who: Privileged Role Admin (Exchange Admin for step 8). Gate: Security.

13. **Import PEST** from the in-VNet agent, signed in with Entra: `import_workbook.py --tenant PEST --resolve --tenant-id <PEST tenant id>`. Then give people their roles with `register_user.py --project PEST --user <sign-in name> --roles …`.
    Who: platform engineer with PMO. Gate: data owner.

14. **Contact inventory** for PEST in the app (Security, ID team, role assignment team, PMO, Operators, escalation managers).
    Who: PMO.

15. **Test mode with mail:** `environment test --redirect <UAT mailbox>`, plus `DIRECT_MAIL_SENDER=GraphMail` and `REQUIRE_ENCRYPTED_SIGNIN_MAIL=true`.
    Who: platform engineer. Gate: mail approval.

16. **Provisioning on** with `--from-sheet PEST` on the real scopes (dry run, then `--approved-by`).
    Who: Subscription Owner and Privileged Role Admin. Gate: Security.

17. **Agent TAP on** (the dry run shows the TAP policy, then `--approved-by`). A new permission can take **up to 24 h** to reach the worker (F61).
    Who: Privileged Role Admin. Gate: Security.

18. **Checks:** `verify_features.sh` from the in-VNet agent, every line PASS except 15 (go-live).
    Who: platform engineer.

19. **UAT end-to-end** (testers on the corporate network or VPN):
    - request → AI review → Owner → Security;
    - the agent creates the user, group and membership;
    - the sign-in mail arrives **encrypted**: inside the tenant in Outlook, outside through the protected-message link;
    - the agent assigns the roles → **Completed**;
    - the completion mail goes to the requester, not the Owner;
    - a red or Prod row is returned.
    Who: UAT testers. Gate: test sign-off.

20. **pest-prod:**
    - repeat steps 9–18 for prod;
    - then `environment prod` (no redirect, encrypted sign-in mail required);
    - line 15 must be PASS;
    - send the go-live mail (template in `docs/GO_LIVE.md`, with the private URL and "corporate network or VPN only").
    Who: DevOps and admins. Gate: go / no-go.

21. **Hypercare:**
    - run `workflow_diag.sh` daily from the in-VNet agent;
    - cost check after 48 h;
    - watch "mail waiting" issues, High "mail reached nobody" issues, and failures to send encrypted mail;
    - watch WAF, firewall and DNS logs.
    Rollback:
    - `provisioning off` or `tap off` (always allowed);
    - `environment test --redirect …`;
    - stop the worker.
    Who: platform engineer.

22. **Vestacy:** repeat steps 5–21 in the Vestacy tenant (169 rows).
    Who: the same roles in the Vestacy tenant. Gate: the same gates.


## 7. Rules that do not change

- The agent never approves and never extends its own rights. Teams can hand tasks back to the agent; it re-checks.
- Every security change: dry run → written approval → `--approved-by`.
- Never paste or log a TAP, password, connection string or personal user id.
- Real workbook data never goes into git.
- No public endpoint in the actual tenant. Every admin action runs from inside the VNet.
