# Go-live in the actual tenant (Cloud Shell, scripts as they are)

Written 23 Oct 2026, for code on branch `claude/great-archimedes-cjr4rl`.
This is the fast path: the pilot scripts run unchanged, in a subscription of the actual tenant. For the hardened enterprise build, see `docs/ENTERPRISE_HANDOFF.md`.
Sections 1–5 deploy a **public** Static Web Apps site, with the scripts as they are today.
**Section 6 is the agreed design: private endpoint only.** It needs prompt A tasks A10–A12 to be built first.


## 1. Who runs it: not a Global Administrator alone

Two facts decide who runs what:
- A **Global Administrator has no rights on Azure subscriptions** by default. The deploy and the Azure role grants need **Owner** (or User Access Administrator) on the subscription.
- Granting Microsoft Graph application permissions to the worker needs **Privileged Role Administrator** (or Global Administrator).

What each person needs (activate it in PIM before the session):
- Deploy, mail, code, import, environment switch: **Owner of the subscription** (Contributor is enough for `app`, `worker`, `environment` and the import).
- Graph permissions (`grant-graph`, and the Graph part of `provisioning on` and `tap on`): **Privileged Role Administrator**.
- `provisioning on` needs **both in the same run**: Privileged Role Administrator, and Owner or User Access Administrator on the role-assignment scope.
- `tap on` also reads the tenant's TAP policy: **Authentication Policy Administrator** or Global Reader. Without either, the dry run can't show the policy.
- `import_workbook.py --resolve` reads the resource names: **Reader** on the subscriptions in the response document.

Simplest setup: one cloud platform admin with PIM-eligible **Privileged Role Administrator + Owner of the subscription** runs every command. A Security approver (not the same person) gives each "approved". A Global Administrator can do it only after being made Owner of the subscription.

Others, in the app or the portal:
- **Authentication Policy Administrator:** turns on the TAP policy.
- **PMO:** fills the Contact inventory.
- **UAT testers:** run the smoke test.


## 2. How account creation and role assignment work

- The admin grants the rights **once**. After that, the worker (Function App) uses its **own managed identity**. Nobody's admin account is used when requests run.
- **Account and group creation:**
  - The worker identity holds Graph application permissions `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All`, plus `UserAuthMethod-TAP.ReadWrite.All` for the TAP.
  - It uses them only after the named Owner and then Security approved, and it re-checks both approvals before every write.
  - It creates accounts only in the project's domain (`--domain-suffix`).
  - It cannot update or delete users, assign licences or invite guests: those permissions and that code don't exist. Licences stay with the ID team, or group-based licensing.
- **Azure role assignment:**
  - The worker identity gets **Role Based Access Control Administrator** at one scope, with a condition: only the allow-listed role IDs, only to users and groups.
  - It never assigns Owner, Contributor, User Access Administrator, RBAC Administrator, Reservations Administrator or Key Vault Data Access Administrator.
  - It assigns the mapping row's roles to the **group**, not to the user.
- **Tenant rule:**
  - A managed identity gets Graph permissions only in its own tenant, and Azure roles only on subscriptions of that tenant.
  - So the worker must run in a subscription of the **actual tenant**, and the target resources must be in that tenant too.
  - If PEST and Vestacy are separate tenants, deploy one stack in each.
- **Delay:** new permissions can take **up to 24 h** to reach the worker (F61). Until then the request waits and says so; nothing fails.


## 3. Limits of this fast path (agree them with Security first)

- **Sign-in:** anyone with a Microsoft account can sign in to the website (SWA Free). They see nothing unless registered, because access is denied by default.
- **Database key:** the Cosmos key sits in the website's app settings.
- **One free tier per subscription:** if the subscription already uses the Cosmos free tier, the deploy fails or is charged. The what-if shows it.
- **Mail limit:** 10 mails an hour from the Azure-managed mail domain, and about 7 mails per request. Mail waits; it is not lost.
- **Fixed names:** the resource group is always `rg-pmo-review-pilot`. That is fine, because it sits in a new subscription.
- **One scope:** `provisioning on` takes one scope and replaces the previous one. Use the narrowest resource group or subscription that holds every resource in the response document. Several scopes need a small code change (enterprise prompt A).
- **Org policy:** your org's Azure Policy may block parts of the deploy. The what-if shows what.
- **AuditLog.Read.All:** `grant-graph` still grants it. It is no longer used, so you can remove it afterwards (a permission change, needs approval).


## 4. Inputs to collect before the session (per tenant)

- `TID`: the actual tenant id (GUID).
- `SUB`: the subscription in that tenant that will host the app.
- `location` and `swaLocation`: for example `centralindia` and `eastasia` (Static Web Apps runs in fewer regions).
- `budgetEmail`, `budgetAmount`, `budgetStartDate` (the first day of a month).
- `DOMAIN`: a **verified** domain of the tenant for new accounts, for example `@pest.example.com` (Entra admin center → Custom domain names).
- `SCOPE`: the narrowest resource group or subscription that holds every resource in the response document, for example `/subscriptions/<id>/resourceGroups/<rg>`.
- Role allow-list: `--from-sheet PEST` (the roles in the mapping sheet), or explicit role definition IDs.
- TAP policy: on, one-time, a target group, and a maximum lifetime of at least `--lifetime` (default 480 minutes).
- `UAT_MAILBOX`: one test mailbox for test mode.
- Contact inventory: mail addresses for Security, ID team, role assignment team, PMO, Operators and the escalation managers.
- People and roles, by sign-in name: Requester, Owner, Security (not the Owner), IdTeam, RoleTeam, PMO, Operator.
- Workbooks: the group classification and the response document (`.xlsx`). Upload them to Cloud Shell; never commit them to git.
- Approvers: name and date for each `--approved-by` (Gokul for cost, Security for permissions).


## 5. Commands in order (Azure Cloud Shell, Bash, signed in to the actual tenant)

**Step 0. Set up the session. Who: the platform admin.**
Switch Cloud Shell to the actual tenant (Portal → Settings → Directories → Switch), then:
```bash
TID=<tenant id>; SUB=<subscription id>; DOMAIN=@<verified domain>; SCOPE=<scope>; UAT=<test mailbox>
az account set --subscription $SUB && az account show --query "{tenant:tenantId, sub:id}" -o table   # tenant must equal $TID
cd ~ && rm -rf onboarding-requests && git clone -q -b claude/great-archimedes-cjr4rl https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests
mkdir -p .local && cp infra/main.parameters.example.json .local/params.json
```
In `.local/params.json`, set `location`, `swaLocation`, `budgetEmail`, `budgetAmount`, `budgetStartDate`, `enableHandoff: true` and `deployWorker: true`.
If the org can't reach github.com, upload the repo bundle instead.

**Step 1. Infrastructure. Who: Owner. Approval: Gokul (cost).**
```bash
tools/deploy_free.sh whatif $SUB .local/params.json        # read-only; send .local/whatif.txt for approval
tools/deploy_free.sh apply $SUB .local/params.json --approved-by "Gokul <date>"
```
If `apply` says it must wait for the policy, wait about 30 minutes and run it again.

**Step 2. Graph read permissions. Who: Privileged Role Administrator. Approval: Security.**
```bash
tools/deploy_free.sh grant-graph $SUB
```

**Step 3. Mail. Who: Owner. Approval: Gokul (cost).**
```bash
tools/deploy_free.sh email $SUB --redirect $UAT                                    # dry run
tools/deploy_free.sh email $SUB --redirect $UAT --approved-by "Gokul <date>"
```

**Step 4. Code. Who: Contributor. No approval.**
```bash
tools/deploy_free.sh worker $SUB && tools/deploy_free.sh app $SUB    # note the new website URL it prints
```

**Step 5. Import the workbooks and register people. Who: Contributor, plus Reader on the response document's subscriptions. Approval: PMO (data owner).**
Upload the two `.xlsx` files (Cloud Shell → Manage files → Upload), then `mv ~/*.xlsx .local/`.
```bash
PY=$(tools/cosmos_python.sh); COSMOS=$(az cosmosdb list -g rg-pmo-review-pilot --query "[0].name" -o tsv)
CS=$(az cosmosdb keys list -g rg-pmo-review-pilot -n $COSMOS --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
imp() { COSMOS_CONNECTION_STRING="$CS" $PY tools/import_workbook.py --tenant PEST --project PEST --project-name "PEST data platform" \
  --classification .local/<classification>.xlsx --response .local/<response>.xlsx --resolve --tenant-id $TID --domain-suffix $DOMAIN "$@"; }
imp            # dry run: check the row counts and the red/amber flags
imp --apply
COSMOS_CONNECTION_STRING="$CS" $PY tools/register_user.py --project PEST --user <sign-in name> --roles Requester   # once per person
unset CS
```

**Step 6. Contact inventory. Who: PMO, in the app.**
Choose tenant PEST → Contact inventory → enter the team addresses separated by `;` → Save.

**Step 7. Test mode. Who: Contributor. No approval.**
```bash
tools/deploy_free.sh environment $SUB test --redirect $UAT
```

**Step 8. Agent creates accounts and assigns roles. Who: Privileged Role Administrator + Owner/UAA on `$SCOPE`. Approval: Security.**
```bash
tools/deploy_free.sh provisioning $SUB on $SCOPE --from-sheet PEST                                  # dry run: lists the permissions and roles
tools/deploy_free.sh provisioning $SUB on $SCOPE --from-sheet PEST --approved-by "<Security name> <date>"
```

**Step 9. Agent TAP.**
First the Authentication Policy Administrator turns on the TAP policy: Entra admin center → Authentication methods → Temporary Access Pass (on, target group, one-time).
Then, who: Privileged Role Administrator. Approval: Security.
```bash
tools/deploy_free.sh tap $SUB on --lifetime 480                                     # dry run shows the policy
tools/deploy_free.sh tap $SUB on --lifetime 480 --approved-by "<Security name> <date>"
tools/deploy_free.sh app $SUB
```

**Step 10 (optional). Privileged rows. Who: Privileged Role Administrator. Approval: Security decides which rows.**
`tools/deploy_free.sh privileged $SUB on` (dry run), then `--approved-by`.

**Step 11. Check. Who: the platform admin. Wait up to 24 h for the new permissions first.**
```bash
bash tools/verify_features.sh $SUB | tee .local/verify.txt     # every line PASS except 15
```

**Step 12. Smoke test. Who: UAT testers.**
1. Submit one PEST Dev request.
2. Approve it as the Owner, then as Security.
3. The agent creates the account and mails the TAP to `$UAT`, then assigns the roles.
4. Check the request reaches **Completed**, and the completion mail names the requester, not the Owner.

**Step 13. Go live. Who: Contributor. Go / no-go: Gokul.**
```bash
tools/deploy_free.sh environment $SUB prod
bash tools/verify_features.sh $SUB | tee .local/verify.txt     # lines 6c, 14 and 15 must be PASS
```
Send the go-live mail from `docs/GO_LIVE.md`, with the **new website URL** from step 4.

**Rollback (always allowed):**
- `tools/deploy_free.sh provisioning $SUB off` or `tools/deploy_free.sh tap $SUB off`;
- `tools/deploy_free.sh environment $SUB test --redirect $UAT`;
- or stop the worker: `az functionapp stop -g rg-pmo-review-pilot -n <worker>`.

**Vestacy:**
- In another tenant: repeat steps 0–13 there with its own `TID`, `SUB`, `DOMAIN`, `SCOPE` and `--tenant VESTACY`.
- In the same tenant: repeat step 5 (`--tenant Vestacy --project VESTACY`), step 6 and step 8 (`--from-sheet VESTACY`) on the same stack.


## 6. Private endpoint path (the agreed design)

**Status:** this path needs code that is not built yet. Prompt A, `docs/ENTERPRISE_CLAUDE_PROMPTS.md`, adds:
- the App Service host (A10);
- encrypted sign-in mail (A11);
- private endpoints in Bicep and admin tools that sign in with Entra (A12).

The steps below use those commands. Where a command name may still change, it is marked *(after A12)*.
Design: `docs/ARCHITECTURE.md` section 12. Settings: `docs/SECURE_HOSTING_AND_MAIL.md`.

### What changes from sections 1–5
- **Where you run commands:** a **jump box** (Azure Bastion) or a **self-hosted Azure DevOps agent** inside the spoke VNet. Cloud Shell outside the VNet can't reach the private app, its deployment site or Cosmos.
- **Database access:** no key. Your own Entra account (or the jump box identity) gets the Cosmos data role. The tools read `COSMOS_ENDPOINT` instead of `COSMOS_CONNECTION_STRING`.
- **Hosting:** App Service (website and API in one app) instead of Static Web Apps. The URL is private: `https://<app>.azurewebsites.net`, which resolves to a private IP.
- **Password and TAP mail:** sent through Graph from a shared mailbox, and encrypted by an Exchange rule.
- **Extra people:**
  - network admin (before you start);
  - Exchange Admin and Compliance Admin (mail);
  - Conditional Access Admin (sign-in policy).

### Extra inputs (on top of section 4)
- `VNET_ID`, plus the subnet ids for the private endpoints and for worker integration.
- The private DNS zone ids, linked to the hub: `privatelink.azurewebsites.net`, `privatelink.documents.azure.com`, `privatelink.vaultcore.azure.net`, and `privatelink.blob/queue/table.core.windows.net`.
- `APP`: the App Service app name.
- `SIGNIN_MAILBOX`: the shared mailbox, for example `onboarding-signin@<domain>`.
- A confirmation that the firewall allow-list is in place: `login.microsoftonline.com`, `graph.microsoft.com`, `management.azure.com`, the ACS endpoint, and monitoring.
- `JUMP`: the jump box or the agent pool name.

### Steps

**P1. Network. Who: network admin.**
- spoke VNet, peering, the private-endpoint subnet and the worker integration subnet *(verify the subnet delegation Flex needs)*;
- private DNS zones linked to the hub;
- on-premises DNS forwarders to the DNS Private Resolver;
- the firewall allow-list.

**P2. Jump box or agent. Who: DevOps.**
- A VM in the VNet (Bastion, no public IP), or a self-hosted agent.
- Install the Azure CLI, Bicep, the .NET 10 SDK, Node.js 22, Python 3.11+, git and zip.
- Clone the repo from the org's repo.

**P3. Session start on the jump box. Who: the platform admin.**
```bash
TID=<tenant id>; SUB=<subscription id>; RG=<resource group>; APP=<app name>; COSMOS=<cosmos account>
az login --tenant $TID --use-device-code
az account set --subscription $SUB && az account show --query "{tenant:tenantId, sub:id}" -o table
cd ~/onboarding-requests && git pull -q && git log --oneline -1
```

**P4. Infrastructure. Who: Owner. Approval: Gokul (cost) and Security (design).**
Run the what-if through the pipeline, or `tools/deploy.sh whatif $SUB <params>` *(after A12)*.
Check that the app, Cosmos, Key Vault and storage show `publicNetworkAccess: Disabled`, each with a private endpoint. Then apply with `--approved-by`.

**P5. Private access check. Who: network admin and DevOps.**
```bash
nslookup $APP.azurewebsites.net            # from a corporate laptop or the jump box: a private IP (10.x / 172.16-31.x / 192.168.x)
nslookup $COSMOS.documents.azure.com       # private IP
curl -sS -o /dev/null -w "%{http_code}\n" https://$APP.azurewebsites.net    # from the jump box: 302 (to the Entra sign-in)
```
Then:
- from a laptop outside the network, the site must **not answer**;
- an account from another tenant must be refused at sign-in.

**P6. Cosmos data role for operators. Who: Owner.** This replaces the Cosmos key.
```bash
az cosmosdb sql role assignment create -g $RG -a $COSMOS --scope "/" \
  --role-definition-id 00000000-0000-0000-0000-000000000002 \
  --principal-id $(az ad signed-in-user show --query id -o tsv)      # Cosmos DB Built-in Data Contributor, for this operator
```
Give it only to named operators. Use a group if there are several.

**P7. Worker rights. Who: Privileged Role Administrator, and Exchange Admin for mail. Approval: Security.**
- The Graph read roles: `tools/deploy.sh grant-graph $SUB` *(after A12; today `deploy_free.sh grant-graph`)*.
- **Mail.Send, scoped to one mailbox**, in Exchange Online PowerShell. Do **not** grant it in Entra *(verify the cmdlets)*:
  ```powershell
  Connect-ExchangeOnline
  New-ServicePrincipal -AppId <worker MI app id> -ObjectId <worker MI object id> -DisplayName "pmo-worker"
  New-ManagementScope -Name "pmo-signin-mailbox" -RecipientRestrictionFilter "PrimarySmtpAddress -eq '<SIGNIN_MAILBOX>'"
  New-ManagementRoleAssignment -App <worker MI app id> -Role "Application Mail.Send" -CustomResourceScope "pmo-signin-mailbox"
  ```
- **Encryption rule. Who: Exchange Admin, with Compliance Admin for the template.** *(Verify the parameters.)*
  ```powershell
  New-TransportRule -Name "PMO sign-in mail encryption" -From "<SIGNIN_MAILBOX>" `
    -HeaderMatchesMessageHeader "x-pmo-sensitivity" -HeaderMatchesPatterns "signin" `
    -ApplyRightsProtectionTemplate "Encrypt"      # or "Do Not Forward"
  ```
- Test: one mail to a test mailbox inside the tenant and one outside. Both must arrive encrypted.

**P8. Import and register people. Who: an operator with the P6 role, on the jump box. Approval: PMO (data).**
```bash
PY=$(tools/cosmos_python.sh); export COSMOS_ENDPOINT=https://$COSMOS.documents.azure.com:443/     # Entra sign-in, no key (after A12)
$PY tools/import_workbook.py --tenant PEST --project PEST --project-name "PEST data platform" \
  --classification .local/<classification>.xlsx --response .local/<response>.xlsx --resolve --tenant-id $TID --domain-suffix @<domain>           # dry run
# same command + --apply
$PY tools/register_user.py --project PEST --user <sign-in name> --roles Requester      # once per person (roles: docs/RUNBOOK.md)
$PY tools/register_user.py --project PEST --list
```

**P9. Contact inventory. Who: PMO, in the app, from the corporate network.**

**P10. Test mode. Who: Contributor.**
```bash
tools/deploy.sh environment $SUB test --redirect <UAT mailbox>     # (after A12)
az functionapp config appsettings set -g $RG -n <worker> --settings DIRECT_MAIL_SENDER=GraphMail DIRECT_MAIL_FROM=<SIGNIN_MAILBOX> REQUIRE_ENCRYPTED_SIGNIN_MAIL=true
```
The `DIRECT_MAIL_*` / `REQUIRE_ENCRYPTED_SIGNIN_MAIL` settings arrive with A11.

**P11. Provisioning and TAP. Who: Privileged Role Admin + Owner/UAA on the scope. Approval: Security.**
Use the same commands as section 5, steps 8–10, run from the jump box (`deploy.sh` *(after A12)*).

**P12. Check and smoke test. Who: platform admin, then UAT testers on the corporate network or VPN.**
```bash
bash tools/verify_features.sh $SUB | tee .local/verify.txt     # every line PASS except 15
```
In the smoke test, the sign-in mail must arrive **encrypted**, and the request must reach **Completed**.

**P13. Go live to prod. Who: Contributor. Go / no-go: Gokul.**
```bash
tools/deploy.sh environment $SUB prod      # (after A12; today deploy_free.sh): no redirect; the encrypted sign-in mail is required
bash tools/verify_features.sh $SUB | tee .local/verify.txt     # lines 6c, 14, 15 PASS
```
Send the go-live mail from `docs/GO_LIVE.md`, with the **private URL** and "open from the corporate network or VPN".

**Rollback (always allowed):** `provisioning off`, `tap off`, `environment test --redirect …`, or stop the worker. All of these run from the jump box.

**Vestacy:** repeat P1–P13 in the Vestacy tenant, or only P8, P9 and P11 if it is in the same tenant.
