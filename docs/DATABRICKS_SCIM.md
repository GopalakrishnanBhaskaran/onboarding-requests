# Databricks SCIM from Microsoft Entra ID (account level)

Script: `tools/databricks/entra_scim_setup.sh`. It is separate from the PMO onboarding app: it creates no Azure resource and costs nothing. Written 31 Oct 2026; facts F105–F109 in `docs/VERIFIED_FACTS.md`, read on Microsoft Learn that day.

## Decide first: SCIM or automatic identity management (F106)

Databricks recommends **automatic identity management (AIM)** over SCIM. AIM is **on by default for accounts created after 1 Aug 2025**. It syncs users, groups, nested groups and service principals with no Entra app.

| Situation | Use |
|---|---|
| AIM is **Enabled** (account console → Security → Identity provider setup) | AIM only. Do not add SCIM: the same people through both become duplicates with permission conflicts. |
| AIM is off and you want Entra to push users and groups (and remove leavers) | SCIM (this script) |
| Users from another Entra tenant (B2B guests), or workspaces without identity federation | SCIM (AIM supports neither) |
| You plan to switch AIM off | Set up SCIM first as the fallback |

## What SCIM does and does not do (F105)

- Syncs the **users and groups assigned to the Entra app**, direct members only. **Nested groups and service principals are not synced**: assign the inner groups too, or use AIM.
- First sync at once, then every **20–40 minutes**.
- A user removed from the app is **deactivated** in the Databricks account and all its workspaces.
- Group renames in Entra do not sync. `userName` must equal the mail (`emails.value`): for guests or aliased mail, change the app's attribute mapping to `userPrincipalName`.
- Only one connector: switch off any **workspace-level** SCIM connectors when the account-level one is on. `plan` lists other connectors it finds.

## Who you need

- **Databricks account admin**: the SCIM token and URL (Premium plan).
- **Entra Cloud Application Administrator** (or Application Administrator): runs the script.
- **Entra ID P1 or P2** to provision groups (users alone work on any edition).

## Steps

### 1. Get the SCIM URL and token (Databricks account admin)

1. Open https://accounts.azuredatabricks.net → **Security** → **Identity provider setup**.
2. Under **Identity management**, click **Set up user provisioning**.
3. Copy the **Account SCIM URL** (`https://accounts.azuredatabricks.net/api/2.1/accounts/<account id>/scim/v2`) and the **SCIM token**.
   - Keep the token private. Paste it only into the script's hidden prompt, never into chat, mail or a ticket.

### 2. Run from Azure Cloud Shell (Bash)

```bash
az account show -o table                     # the Entra tenant of the Databricks account
cd ~ && { [ -d onboarding-requests ] || git clone -q https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git; }
cd ~/onboarding-requests && git fetch -q origin && git checkout -q claude/elegant-darwin-nfot4p && git pull -q
S=tools/databricks/entra_scim_setup.sh
bash $S self-test                            # offline check of the script itself: "self-test passed"

URL='https://accounts.azuredatabricks.net/api/2.1/accounts/<account id>/scim/v2'   # paste the copied URL between the quotes
bash $S plan --scim-url "$URL" --group "<Entra group 1>" --group "<Entra group 2>"
```

`plan` changes nothing. It:
- asks for the token (hidden) and checks it against the SCIM URL with one read;
- signs in to Graph;
- prints what `apply` would do and flags nested groups and other connectors.

Check the plan, get the approval, then run the same command as `apply`:

```bash
bash $S apply --scim-url "$URL" --group "<Entra group 1>" --group "<Entra group 2>" --approved-by "<name> <date>"
bash $S status                               # job state, last run, users and groups synced so far
```

Notes:
- `--group` takes a group's exact display name, or its object id.
- `--app-name` changes the enterprise app's name (default *Azure Databricks SCIM Provisioning*).
- Running `apply` again only adds what is missing: groups, credentials, start. Run it again to add groups or to replace an expired token.
- **Sign-in to Graph.** The script uses the Azure CLI's sign-in if its token carries the four permissions below. Otherwise it shows a device code: open the link, enter the code and sign in as the Cloud Application Administrator. The first time, Entra may ask for consent for *Microsoft Graph Command Line Tools*.

### 3. Check in Databricks

After 5–40 minutes, the account console → **User management** shows the users and groups. Then add the groups to workspaces (workspace assignment is separate from provisioning).

## What the script changes (apply only)

| Change | Graph call | Permission used (delegated) |
|---|---|---|
| Enterprise app from the gallery app *Azure Databricks SCIM Provisioning Connector* (reused if one with the name exists) | `POST /applicationTemplates/{id}/instantiate` | `Application.ReadWrite.All` |
| Assignment required → No (Databricks recommends it) | `PATCH /servicePrincipals/{id}` | `Application.ReadWrite.All` |
| Provisioning job from the app's template | `POST …/synchronization/jobs` | `Synchronization.ReadWrite.All` |
| Test, then save, the SCIM URL and token (`BaseAddress`, `SecretToken`) | `POST …/validateCredentials`, `PUT …/synchronization/secrets` | `Synchronization.ReadWrite.All` |
| Assign each group | `POST /servicePrincipals/{id}/appRoleAssignedTo` | `AppRoleAssignment.ReadWrite.All` |
| Start provisioning | `POST …/jobs/{id}/start` | `Synchronization.ReadWrite.All` |

Reads in `plan` and `status` also use `GroupMember.Read.All`, for groups and nested groups.

**The token** is read from a hidden prompt, or from `DATABRICKS_SCIM_TOKEN` if set. It goes only to the Databricks SCIM read and to Entra's test and save calls. It is never printed, logged or put on a command line; the self-test proves that.

## Stop or undo

- **Pause** (always allowed): `bash $S pause`. Users already in Databricks stay; nothing more syncs.
- **Remove**: Entra admin center → Enterprise applications → the app → **Delete**.
  - Users already provisioned stay in Databricks.
  - Removing a user or group from the app first deactivates them there.
- **Token rotation**:
  1. generate a new token in the account console;
  2. run `apply` again with the same options.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `plan` stops: "Databricks refused the SCIM token" | The token expired, was regenerated, or is from another account: get a new one (step 1). |
| Device code sign-in fails or is blocked | Conditional Access may block the device code flow. Ask for an exception for this sign-in, or run from a session whose Azure CLI token carries the four permissions (the script says which are missing). |
| `status` shows QUARANTINED | Usually a bad or expired token: get a new one and run `apply` again. |
| A user is not created | `userName`/mail mismatch (guests, aliases): map `userName` to `userPrincipalName` in the app's provisioning attribute mapping. Or the user is only in a nested group: assign that group too. |
| Changes take long | The provisioning cycle is 20–40 minutes. For an immediate run: the app → Provisioning → *Restart provisioning*. |
