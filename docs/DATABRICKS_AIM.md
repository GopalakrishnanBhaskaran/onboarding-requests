# Databricks automatic identity management (AIM) from Microsoft Entra ID

Script: `tools/databricks/aim_setup.sh`. Separate from the PMO onboarding app: no Azure resource, no cost.
Written 31 Oct 2026. Facts F110–F113 in `docs/VERIFIED_FACTS.md`, read on Microsoft Learn that day.
Use this instead of SCIM (`docs/DATABRICKS_SCIM.md`) unless you need cross-tenant (B2B) users.

## What AIM gives you (F106, F110)

- Azure Databricks reads users, groups, **nested groups** and **service principals** straight from Entra ID. No Entra app, no SCIM token, no Entra admin role.
- Entra is the source of truth: group membership changes apply within about 5 minutes at browser sign-in (up to 40 minutes for jobs and tokens).
- Members are added **just in time**, when they first sign in. A group appears in the account when you add it, then you assign it to a workspace.
- **On by default for accounts created after 1 Aug 2025.** Older accounts switch it on once.

## The one manual step: the switch (F110)

Microsoft Learn documents **no API, CLI or Terraform setting** to turn AIM on. It is a toggle in the account console, so the script can't flip it; it prints the steps and proves afterwards that AIM works.

Needs:
- Premium tier;
- a single Entra tenant (no cross-tenant identities);
- at least one **identity-federated** workspace (badge on the account console's Workspaces page);
- a Databricks **account admin**.

In https://accounts.azuredatabricks.net → **Security** → **Identity provider setup** → **Identity management**:
1. **Readiness report:** start a scan, wait, and fix what it lists.
   - It catches external-ID mismatches that would create duplicate users or groups.
   - Needed if SCIM or manually created groups were used.
2. Toggle **Automatic identity management** to **Enabled**. Allow 5–10 minutes.

**If SCIM is already running:** keep it running next to AIM (Databricks' guidance). Pause it later, and only if:
- there are no nested groups;
- the readiness report is clean;
- nobody edited group memberships by hand in Databricks.

Pause it with `bash tools/databricks/entra_scim_setup.sh pause`.

## Commands (Azure Cloud Shell, Bash)

Sign in as a **Databricks account admin**, in the Entra tenant of the Databricks account.

```bash
az login --tenant <tenant id>               # only if Cloud Shell is not already in that tenant
az account set --subscription <subscription with the workspaces>
cd ~ && { [ -d onboarding-requests ] || git clone -q https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git; }
cd ~/onboarding-requests && git fetch -q origin && git checkout -q claude/elegant-darwin-nfot4p && git pull -q
A=tools/databricks/aim_setup.sh
bash $A self-test                           # offline: "self-test passed"

ACC='<Databricks account id>'               # account console > your user menu (top right), a GUID
bash $A plan --account-id "$ACC"            # read-only: admin check, workspaces, SCIM apps, the console steps
```

Then do the console steps above. After AIM is on:

```bash
bash $A plan  --account-id "$ACC" --workspace <workspace name> --group "<Entra group>" --group "<Entra admin group>"
bash $A apply --account-id "$ACC" --workspace <workspace name> --group "<Entra group>" --approved-by "<name> <date>"
bash $A apply --account-id "$ACC" --workspace <workspace name> --group "<Entra admin group>" --admin --approved-by "<name> <date>"
bash $A status --account-id "$ACC" --workspace <workspace name>
```

- `--workspace` takes the workspace's Azure resource name (in the current subscription; `--subscription` for another) or its numeric id (the number in `adb-<id>.<n>.azuredatabricks.net`).
- `--group` takes the Entra group's display name or object id.
- `apply`:
  - adds each group to the Databricks account through the external group API, which is also the proof that AIM is on. While AIM is off it stops before assigning anything (F111).
  - assigns the group to the workspace as **USER**, or **ADMIN** with `--admin` (F112).
  - Running it again changes only what is missing, and never lowers ADMIN to USER.
- `plan` does not call the external group API, because reading a group adds it to the account.

## Check it works

- Account console → **User management** → **Groups**: the group shows as external.
- A member signs in to the workspace: they're added on first sign-in (status **Active**).
- Optional, in a SQL warehouse, to see AIM activity:
```sql
SELECT action_name, count(*) AS n
FROM system.access.audit
WHERE request_params.endpoint = 'autoUserCreation' AND event_time > current_timestamp() - INTERVAL 2 DAYS
GROUP BY action_name ORDER BY n DESC;
```

## Good to know (F106, F110)

- **Nested groups:** a parent group assigned to a workspace gives its nested members access. To share workspace objects (notebooks, queries) with a child group, assign that child group to the workspace too.
- **Group renames** in Entra show in Databricks when an admin opens the group in the account console. An `apply` refreshes them too.
- **Deleted Entra users** can't sign in. Revoke their personal access tokens (Databricks does not).
- **Email changes** create a new Databricks user; contact Databricks support.
- **Undo:** remove the group from the workspace in the account console (Workspaces → the workspace → Permissions).
  - Switching AIM off removes all members from the AIM-managed groups. Set up SCIM first if you ever need to.

## Troubleshooting

| Message | Fix |
|---|---|
| "you are not an account admin" (403) / "did not accept your sign-in" (401) | Sign in as a Databricks account admin, in the account's tenant (`az login --tenant …`). |
| "could not read Entra group … not on yet" | AIM is off or still propagating: do the console toggle and wait 5–10 minutes. |
| "reading the assignments … failed" / assigning failed | The workspace is not identity-federated (account console → Workspaces), or the numeric id is wrong. |
| "workspace …: 0 found" | The workspace is in another subscription: add `--subscription <id>`, or pass its numeric id. |
