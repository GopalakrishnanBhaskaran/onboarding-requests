# Deploy from a Mac (instead of Azure Cloud Shell)

Cloud Shell resets its home folder after a short idle time, so the clone and the workbooks disappear. On a Mac they stay.
The commands are the same as in Cloud Shell. Each Azure permission or setting change still needs a dry run and an explicit "approved".

## Once
1. Homebrew (skip if `brew --version` works). It asks for your password:
   ```bash
   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
   ```
2. Clone the repository and run the set-up:
   ```bash
   git clone -b claude/great-goodall-j4ozev https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
   bash ~/onboarding-requests/tools/mac_setup.sh 5b9f9810-0512-49e5-b7f7-0159bc488570
   ```
   - The first `git` command may ask to install the Xcode command line tools: accept, then run it again.
   - `mac_setup.sh` installs ready-built tools only, so nothing is compiled:
     - `az` from Microsoft's Homebrew cask (needs macOS 13 or later);
     - Node 22 from nodejs.org, checksum-checked, into `~/.pmo-tools/node`;
     - the Mac's own `python3` (3.9 or later).
   - On an Intel Mac, Homebrew has had no new ready-built packages since September 2026. A plain `brew install python` or `node` may then compile for an hour or more with no output (F69).
   - It is safe to run again; it skips what is already installed.
   - It also installs Rosetta 2 on Apple silicon. The Static Web Apps upload client is an Intel build (F69).
   - It installs the .NET 10 SDK into `~/.dotnet` and the Python packages into `.local/venv`.
   - It signs you in to Azure (browser) and writes `.local/params.json`.
   - It changes nothing in Azure.
   - If the subscription is not visible, run `az login --tenant <tenant id>` and re-run the script.

## Every session
Open Terminal and type `bash` first. In zsh, the Mac's default shell, a variable such as `$COMMON` is not split into separate arguments, so the import commands would fail.
```bash
bash
cd ~/onboarding-requests && SUB=5b9f9810-0512-49e5-b7f7-0159bc488570 && az account set --subscription $SUB
git pull -q origin claude/great-goodall-j4ozev && git log --oneline -1
```

If a command says `az (Azure CLI) not found` or `az: command not found`, the set-up has not run on this Mac yet. Run `bash tools/mac_setup.sh $SUB`, open a **new** Terminal window, type `bash` and start again. The tools find Homebrew's `az` themselves once it is installed.

## Redeploy the code (no approval)
```bash
tools/deploy_free.sh worker $SUB
tools/deploy_free.sh app $SUB
```
The first run builds everything: about 5–10 minutes. Expect `Deployed commit <sha> to https://…azurestaticapps.net`.

## Let the AI agent do privileged rows (security changes: dry run, then "approved")
Example: PEST row `PH-DATAENGINEERS-DEV` (Storage Blob Data Contributor, Key Vault Secrets Officer, Reader).
1. Add its roles to the agent's allow-list. This replaces the constrained RBAC Administrator assignment on the test resource group.
   ```bash
   ROLES=acdd72a7-3385-48ef-bd42-f606fba81ae7,2a2b9908-6ea1-4ae2-8e65-a410df84e7d1,ba92f5b4-2d11-453d-a403-e96b0029c9fe,b86a8fe4-44ce-4948-aee5-eccb2c155cd7
   tools/deploy_free.sh provisioning $SUB on /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access $ROLES
   # after "approved":
   tools/deploy_free.sh provisioning $SUB on /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access $ROLES --approved-by "Gokul <date>"
   ```
   The role ids are Reader, Storage Blob Data Reader, Storage Blob Data Contributor and Key Vault Secrets Officer.
2. Switch privileged rows on:
   ```bash
   tools/deploy_free.sh privileged $SUB on
   # after "approved":
   tools/deploy_free.sh privileged $SUB on --approved-by "Gokul <date>"
   ```
3. What happens:
   - The agent's next run (every 5 minutes) takes back an ID-team task it left only because the row was privileged, if nobody has picked it up.
   - It creates the account, mails the sign-in details (agent TAP), adds the membership and assigns the three roles.
   - Owner, Contributor, User Access Administrator and the other privileged administrator roles always stay with people.
4. Switch off at any time: `tools/deploy_free.sh privileged $SUB off`.

## Check
```bash
bash tools/verify_features.sh $SUB | tee .local/verify.txt
bash tools/workflow_diag.sh $SUB
```
- Line 1 shows WAIT until the first `app` and `worker` deploy from this Mac. The deploy stamps from Cloud Shell are gone.
- Line 5c covers privileged rows.

## Workbooks and imports
- Copy the two workbooks into `~/onboarding-requests/.local/` once. They stay there; `.local/` and `*.xlsx` are git-ignored.
- Run the import commands from `docs/NEXT_SESSION.md` step 4, inside `bash`.
- An `--update` re-import overwrites the edits you made to rows in the app, such as a corrected group object id.
