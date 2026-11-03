# Cloud Shell session start (PEST and Vestacy)

Do this whenever Claude asks you to run something in Cloud Shell. Do it again after every new or restarted Cloud Shell.
Cloud Shell ends after 20 minutes idle and deletes your files (FB37).

## 1. Sign in (you do this, never Claude)

1. Use a separate browser profile (or InPrivate window) for each tenant, so PEST and Vestacy sign-ins don't mix.
2. Open https://portal.azure.com and sign in with the account for that tenant. MFA is yours.
3. Settings (gear) > **Directories + subscriptions** > **Switch** to the PEST or the Vestacy directory.
4. Open Cloud Shell (`>_` at the top) > **Bash**.
   The first time: **No storage account required** > pick the subscription > **Apply**.

## 2. Paste the block for that tenant

### PEST (code from the PEST repo only)

```bash
pestgit() { GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=http.extraHeader \
  GIT_CONFIG_VALUE_0="Authorization: Bearer $(az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv)" git "$@"; }
if [ -d ~/pest-admin-platform/.git ]; then pestgit -C ~/pest-admin-platform pull -q
else pestgit clone -q -b feature/fabric-adls-poc https://dev.azure.com/EHPestADO/pest-data/_git/pest-admin-platform ~/pest-admin-platform; fi
cd ~/pest-admin-platform && source fabric-adls-poc/tools/cloud_init.sh pest
```

- This signs in to the repo with your Entra account. No PAT is used, and the token stays in memory.
- If the clone says the branch `feature/fabric-adls-poc` does not exist, or Claude says the tools changed, run section 4 first.

### Vestacy (code from GitHub, read-only; nothing is pushed to a Vestacy repo)

```bash
[ -d ~/fabric-poc/.git ] || git clone -q -b claude/friendly-pasteur-vt5a4u https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/fabric-poc
git -C ~/fabric-poc pull -q
cd ~/fabric-poc && source fabric-adls-poc/tools/cloud_init.sh vestacy
```

- **First run:**
  - It shows `CHECK` with the tenant name. Confirm that it is Vestacy.
  - From the next run, add `--tenant-prefix <the 4 characters it shows>`.
- **More than one subscription:** it shows `CHOOSE` and lists them. Run it again with `--sub "<name>"`.
- **Separate state:** Vestacy has its own state file (`.local/state-vestacy.json`). It starts empty: no approvals and no ids.
  Claude starts with the read-only `discover.sh`. PEST approvals never apply to Vestacy.

## 3. Paste the `== READY` block back to Claude

Each line starts with OK, CHECK, CHOOSE, WARN or FAIL. The block holds no token and no full id, so it is safe to paste.

| You see | Why | Fix |
|---|---|---|
| `FAIL signed in to tenant …, but profile … is tenant …` | Cloud Shell is in the other directory | Step 1.3, reopen Cloud Shell, paste the block again |
| `FAIL profile vestacy, but you are signed in to the pest tenant` | Same | Same |
| `FAIL az is not signed in` | Cloud Shell session expired | Restart Cloud Shell (Restart icon) |
| `fatal: Authentication failed` or `TF400813` on the PEST clone | The PEST Azure DevOps org did not accept this Entra sign-in (UNVERIFIED until the first clone) | Open the repo in the browser with the same account. If that fails, ask the PEST ADO admin for read access to `pest-admin-platform` |
| `Remote branch feature/fabric-adls-poc not found` | The PEST branch is not created yet | Section 4, first time |
| `CHOOSE n enabled subscriptions` | The state names no storage account yet | Run the block again with `--sub "<name>"` |
| `WARN no Fabric API token` | This account has no Fabric access | Storage steps still work. For Fabric, use an account with a Fabric licence |
| Any tool: `state … is for tenant X, but you are signed in to Y` | The shell holds the other tenant's state | Run the right tenant block again |

## 4. Copy the tools to the PEST repo (first time, and after Claude changes them)

Run it in the PEST directory, from any folder:

```bash
[ -d ~/fabric-poc/.git ] || git clone -q -b claude/friendly-pasteur-vt5a4u https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/fabric-poc
git -C ~/fabric-poc pull -q
P=https://dev.azure.com/EHPestADO/pest-data/_git/pest-admin-platform
bash ~/fabric-poc/fabric-adls-poc/tools/push_to_ado.sh $P --pr       # first time: new branch + pull request
bash ~/fabric-poc/fabric-adls-poc/tools/push_to_ado.sh $P --update   # later: adds a commit to the same branch
```

- The tool exports only the PEST part.
- It refuses Vestacy repos and any file that mentions Vestacy, a key, a password, a personal id or a full public IP.
- Then run the PEST block in section 2.

## What a restart loses

- **Lost:** the clone and `fabric-adls-poc/.local/`, which holds the working state (wave approvals, ids found) and the raw output.
- **Kept:** everything in Azure and Fabric.
- After the session start, Claude gives the `state.py approve` line again for each wave you approved in chat.

Keeping files across restarts would need a storage account mounted in Cloud Shell. That is a cost and needs approval, so it is not set up.

## Other tools in this repo (PMO portal)

Use the same sign-in steps (section 1). Then use the session start in `docs/RUNBOOK.md`.
