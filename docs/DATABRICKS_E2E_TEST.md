# Databricks end-to-end test: control plane and data plane, Reader and Data Engineer groups

Written 1 Nov 2026, automated 2 Nov 2026, **Data Engineer group added 2 Nov 2026**. Facts: F106, F110–F118, F120 (`docs/VERIFIED_FACTS.md`). Tools: `tools/databricks/aim_setup.sh` (`docs/DATABRICKS_AIM.md`).
Log every result in `docs/DEPLOYMENT_LOG.md` as **live check V** (`V-reader`, `V-engineer`). About 1–1.5 hours the first time, plus about 30 minutes for the second group.

## The model being tested

Databricks access is set up **once per Entra group**. After that, each joiner needs only **membership of that group**, which the AI agent adds after both approvals. Automatic identity management (AIM) carries the membership into Databricks when the joiner first signs in: within about 5 minutes for the browser, up to 40 minutes for jobs and tokens (F106).

**A3–A5 are automated (2 Nov 2026)**, with the same rules in both ways (`config/databricks-access.json`, decided by the **group name**):

| Group name says | Entitlements (A4) | Unity Catalog (A5) |
|---|---|---|
| reader, viewer, view, consumer, read | Workspace access + **Databricks SQL access** | catalog `USE_CATALOG`; schema `USE_SCHEMA`, `SELECT` |
| writer, contributor, editor, readwrite | Workspace access | + `MODIFY` |
| engineer, developer, scientist | Workspace access | + `MODIFY`, `CREATE_TABLE` |
| admin, owner | never automated: a person decides | — |
| none of these, or two levels | left to the role assignment team | — |

- **By hand, now:** `aim_setup.sh apply … --grant <catalog>.<schema>` does A3, A4, A5 and the tests and appends the result to `.local/databricks-setup.log`.
- **Automatically, after approval:** with `deploy_free.sh databricks … on` (dry run → "approved"), the **AI agent** does A3–A5 at *With Role Team* for every request whose mapping row's role scope is `dbx:<workspace>/<catalog>[.<schema>]`. It runs only after both approvals, tests everything live (`DBX_WORKSPACE`, `DBX_ENTITLEMENTS`, `DBX_UC_CATALOG`, `DBX_UC_SCHEMA`) and logs it in the request's trail, its checks and the audit log. Any failed test goes to the role assignment team, with an issue. The verification re-runs the same tests on every role-team report, whoever did the work.

| Plane | What | Who | When |
|---|---|---|---|
| Control: Entra | user, group membership | AI agent after Owner + Security approval (else the ID team) | every joiner |
| Control: Azure RBAC | none needed to use Databricks. If wanted, **Reader** on the workspace resource only, on a row and group of its own; Contributor or Owner makes the user a **workspace admin** (F116) | role assignment team | optional |
| Data: account + workspace (A3) | group added via AIM, assigned USER | AI agent (or `aim_setup.sh apply`) | once per group |
| Data: entitlements (A4) | by group name (F114: not inherited since 14 Sep 2026) | AI agent (or `aim_setup.sh apply`) | once per group |
| Data: Unity Catalog (A5) | by group name (F115, F117) | AI agent (or `aim_setup.sh apply --grant`) | once per group |
| Data: compute (A6) | readers: SQL warehouse **Can use**; engineers: cluster **Can restart** | workspace admin | once per group |
| Data: the user | created in Databricks on first sign-in, status Active | AIM, automatic | every joiner |

## The two test groups

Run the Reader test first; the Data Engineer test is the same steps with the values in the right column. Each group has its own row, its own request and its own evidence.

| | Reader | Data Engineer |
|---|---|---|
| Entra group | `azr-pes-rg-data-dev-dbw-weu-datareaders` | `azr-pes-rg-data-dev-dbw-weu-dataengineers` |
| Level from the name | `datareaders` → **Reader** | `dataengineers` → **Engineer** |
| Mapping row access level | `Data Reader` | `Data Engineer` |
| Entitlements (A4) | Workspace access + Databricks SQL access | **Workspace access only** (no Databricks SQL access) |
| Unity Catalog on `sample_e2e` (A5) | catalog `USE_CATALOG`; `curated`: `USE_SCHEMA`, `SELECT` | catalog `USE_CATALOG`; `curated`: `USE_SCHEMA`, `SELECT`, `MODIFY`, `CREATE_TABLE` |
| Compute (A6) | SQL warehouse, **Can use** | all-purpose cluster, **Can restart** |
| Where the joiner tests (C3) | SQL editor | a notebook on the cluster |
| Must work | read `curated` | read, insert into and create tables in `curated` |
| Must fail | write anything; read `restricted` | read or create in `restricted` |

Why the engineer uses a notebook: *Workspace access* covers notebooks, jobs and pipelines; *Databricks SQL access* covers the SQL editor, queries and SQL warehouses (F120). The policy gives engineers no Databricks SQL access, so they work on clusters.

**Do not reuse the existing `azr-pes-rg-data-dev-ph-weu-dataengineers` group.** Its mapping rows carry Azure roles (Contributor and others), so the agent leaves a Databricks request on them to people (rule 1), and the review treats them as Privileged. The test uses a Databricks-only group.

## Rules this test respects

1. **One row per plane.** A `dbx:` row carries **no Azure role**. If it does, the agent leaves the request to the role assignment team (*one row per plane*), so a request is never completed with half of its access in place.
2. **The group exists before the request.** A group still to be created resolves as *Unresolved* on the request form and cannot be requested.
3. **The name decides the level.** A group name with two levels (for example `…-readers-engineers`) or an admin/owner word is left to people. Keep exactly one level word in each test group's name.

## Prerequisites (check before starting)

- [ ] **A Databricks workspace**: Premium, Unity Catalog, identity-federated, in the **agent's tenant** (pilot: `dbw-pmo-pkt2ofpqmlf3e`; its approval and cost line are still to be recorded, `DEPLOYMENT_LOG.md` 2 Nov 2026).
- [ ] **People**: a Databricks account admin, a workspace admin and a catalog owner (can be one person). In the app: the Gmail tester (Requester, Owner, RoleTeam, IdTeam, PMO) and the pilot admin (Security).
- [ ] **AIM on**: `bash tools/databricks/aim_setup.sh plan --account-id "$ACC"`. If it is off: the console toggle (`docs/DATABRICKS_AIM.md`).
- [ ] **The new code is deployed**: `verify_features` line 1 PASS.
- [ ] **Test mailboxes you can open**, one per joiner, for the Enterprise ID (with the agent TAP on, the one-time pass is mailed there; personal domains such as gmail.com are refused and go to the ID team).

## A. One-time setup per test group

**A1. Entra groups** (Entra admin center → Groups → New group): Security type, one per group, with the names from *The two test groups* (both match the PEST naming rule `azr-pes-rg-data-(dev|tst|ppd|prd)-<x>-weu-<name>`). Copy each **object id**.

**A2. Mapping rows** (PMO in the app → *Mapping sheet* → *Add a row*, tenant PEST), one per group:

| Field | Reader row | Data Engineer row |
|---|---|---|
| Resource | `Databricks PEST Dev` | `Databricks PEST Dev` |
| Access level | `Data Reader` | `Data Engineer` |
| Entra group | `azr-pes-rg-data-dev-dbw-weu-datareaders` | `azr-pes-rg-data-dev-dbw-weu-dataengineers` |
| Role | `Databricks workspace user` | `Databricks workspace user` |
| Role scope | `dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated` | `dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated` |
| Source reference | `E2E test 2026-11-02` | `E2E test 2026-11-02` |
| Privileged / Sensitive / Group to be created | not ticked | not ticked |

Role scope is workspace / catalog . schema; without `.schema` it covers the whole catalog. Then **Edit** each row and paste its **Group object id** from A1. Leave the *Azure roles* box empty (rule 1). Keep the role text free of words like Contributor or Admin: the review would ask for the row to be flagged Privileged.

**A2b. Test data** (SQL editor, once, as the catalog owner or a metastore admin; SAMPLE data only; re-run it to reset the data after a test):
```sql
CREATE CATALOG IF NOT EXISTS sample_e2e;   -- add MANAGED LOCATION '<abfss path>' if the metastore has no default storage
CREATE SCHEMA  IF NOT EXISTS sample_e2e.curated;
CREATE SCHEMA  IF NOT EXISTS sample_e2e.restricted;
CREATE OR REPLACE TABLE sample_e2e.curated.orders      AS SELECT 1 AS id, 'SAMPLE' AS note;
CREATE OR REPLACE TABLE sample_e2e.restricted.salaries AS SELECT 1 AS id, 'SAMPLE' AS note;
```

**A3–A5. Account and workspace (A3), entitlements (A4), Unity Catalog (A5): choose the way.**

*Way 1, by hand now* (Cloud Shell, as a Databricks account admin who is also a workspace admin and owns `sample_e2e`):
```bash
cd ~/onboarding-requests && git pull
ACC='<Databricks account id>'; WS=dbw-pmo-pkt2ofpqmlf3e; A=tools/databricks/aim_setup.sh
for G in azr-pes-rg-data-dev-dbw-weu-datareaders azr-pes-rg-data-dev-dbw-weu-dataengineers; do
  az ad group show --group "$G" --query "{name:displayName, id:id}" -o table          # A1 must exist
  bash $A plan --account-id "$ACC" --workspace "$WS" --group "$G" --grant sample_e2e.curated
done
```
Check the `by name:` line of each plan before applying:
- Reader: `by name: Reader -> entitlements databricks-sql-access, workspace-access; on sample_e2e.curated: catalog USE_CATALOG, schema USE_SCHEMA, SELECT`
- Data Engineer: `by name: Engineer -> entitlements workspace-access; on sample_e2e.curated: catalog USE_CATALOG, schema USE_SCHEMA, SELECT, MODIFY, CREATE_TABLE`

If a line says `left to a person`, the name has no level word or two of them (rule 3): fix the group name, not the policy. Then apply each group:
```bash
bash $A apply --account-id "$ACC" --workspace "$WS" --group azr-pes-rg-data-dev-dbw-weu-datareaders   --grant sample_e2e.curated --approved-by "Gokul 2026-11-02"
bash $A apply --account-id "$ACC" --workspace "$WS" --group azr-pes-rg-data-dev-dbw-weu-dataengineers --grant sample_e2e.curated --approved-by "Gokul 2026-11-02"
```
Expect, for each group, `PASS DBX_WORKSPACE`, `PASS DBX_ENTITLEMENTS`, `PASS DBX_UC_CATALOG` and `PASS DBX_UC_SCHEMA`, followed by a log line ready for `DEPLOYMENT_LOG.md`. Any FAIL exits non-zero.

*Way 2, the agent does it for every request* (security change: dry run, then "approved"):
```bash
tools/deploy_free.sh databricks $SUB on --account-id "$ACC" --workspace dbw-pmo-pkt2ofpqmlf3e --catalogs sample_e2e
```
One switch covers both groups: the agent reads the level from each request's group name. The dry run prints the worker identity's client id and the **Databricks-side rights** an account admin gives it once (F118):
1. Service principal (Entra ID managed).
2. **Account admin** (needed for A3).
3. **Admin** on the workspace (needed for A4).
4. `USE CATALOG` and `MANAGE` on `sample_e2e` (needed for A5).

Then run the same command with `--approved-by "Gokul <date>"`, run `tools/deploy_free.sh app $SUB` (the website must follow the switch), and check `verify_features` line 18: PASS. With Way 2, step B5 needs no person: the agent sets up and tests the group.

**A6. Compute** (workspace admin; costs DBUs and VM time only while running; stop it after the test):
- **Reader group:** SQL warehouses → the smallest warehouse with the shortest auto-stop → Permissions → `…-datareaders`: **Can use**.
- **Data Engineer group:** Compute → *Create compute*: access mode **Standard** (Unity Catalog), single node, the smallest node type, *Terminate after 10 minutes of inactivity* (the minimum). Then on the compute's row: ⋮ → **Edit permissions** → `…-dataengineers` → **Can restart** → *Add* → *Save*. *Can restart* lets the joiner start it again after auto-termination; *Can attach to* would leave starting it to an admin (F120).

## B. The joiner, through the app (once per group)

1. **Requester** (Gmail tester) → *New request*: tenant PEST, Enterprise ID (a test mailbox you can open), first and last name, environment **Dev**, resource **Databricks PEST Dev**. Under *Access this resolves to*, tick **only the group under test** (both rows share the resource). *Insert into justification* and fill it in, Owner = the Gmail tester's sign-in name → *Submit request*. Expect *Awaiting Owner* within seconds (or after the worker's Entra check, within 5 minutes).
   - **Reader:** a reading task is enough, e.g. *Why: to read the curated orders for the monthly report.*
   - **Data Engineer:** name the build work; a write level whose justification describes only reading is returned. E.g. *Why: to build and test ingestion pipelines that write curated tables in sample_e2e.curated.*
2. **Owner** (Gmail tester) → *My actions* → *Open to decide* → tick → *Approve business need*. With the agent TAP on, the panel shows *Sign-in details go to* the Enterprise ID: check it is the test mailbox.
3. **Security** (pilot admin) → *My actions* → *Approve risk and access level*.
4. **AI agent** (within about 5 minutes): creates the user `<first.last>@gopalakrishnanbaskarangmail.onmicrosoft.com` and adds it to the group.
   - **Agent TAP on:** it mails the one-time pass, the initial password and the steps to the Enterprise ID. The joiner signs in with the pass, adds Microsoft Authenticator and changes the password straight away (step 4 of the mail).
   - **Agent TAP off, or the address was refused:** the **ID team** issues the one-time pass: Entra admin center → the user → Authentication methods → Add → Temporary Access Pass (one-time). Then in the app: *Pick up task*, record only the issued time, expiry and one-time flag → *Record completion*.
   - Never type the pass into the app, mail or chat.
5. **Role assignment step.** *Way 2:* the agent sets the group up in Databricks and tests it (the trail shows `agent-databricks-setup … as Reader` or `… as Engineer`, then `agent-databricks-tests`). *Way 1:* the task says the agent left it; keep the prefilled line `Databricks workspace user | dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated` → *Record completion*. With Way 2 switched on, the verification then tests the group live in Databricks (DBX_* checks on the role task).
6. **AI verification** → **Completed**; one completion mail (redirected to the test mailbox).

## C. Evidence (once per group)

**C1. Control plane** (Cloud Shell; `G` = the group under test):
```bash
UPN='<new user principal name>'; G='azr-pes-rg-data-dev-dbw-weu-dataengineers'   # or …-datareaders
az ad user show --id "$UPN" --query "{upn:userPrincipalName, enabled:accountEnabled}" -o table
az ad group member check --group "$G" --member-id "$(az ad user show --id "$UPN" --query id -o tsv)" -o table   # value: true
```
In the app: the request page shows the journey at *Verified* and the final verification checks.

**C2. First sign-in** (InPrivate window): open `https://adb-<id>.<n>.azuredatabricks.net` and sign in with the UPN and the new password (or the one-time pass if not used yet). Expect to land in the workspace. If you see an entitlement error, A4 is missing (`DBX_ENTITLEMENTS`).

**C3a. Data access, Reader joiner** (SQL editor, the A6 warehouse):
```sql
SELECT current_user();                                                    -- the joiner's UPN
SELECT is_account_group_member('azr-pes-rg-data-dev-dbw-weu-datareaders'); -- true
SELECT * FROM sample_e2e.curated.orders;                                  -- 1 row
INSERT INTO sample_e2e.curated.orders VALUES (2, 'SAMPLE reader');        -- must FAIL: no MODIFY
SELECT * FROM sample_e2e.restricted.salaries;                             -- must FAIL: permission denied
```

**C3b. Data access, Data Engineer joiner** (Workspace → *Create* → *Notebook*, attach it to the A6 cluster and start it if it is terminated; one `%sql` cell per line):
```sql
%sql SELECT current_user();                                                       -- the joiner's UPN
%sql SELECT is_account_group_member('azr-pes-rg-data-dev-dbw-weu-dataengineers'); -- true
%sql SELECT * FROM sample_e2e.curated.orders;                                     -- 1 row
%sql INSERT INTO sample_e2e.curated.orders VALUES (2, 'SAMPLE engineer');         -- works: MODIFY
%sql CREATE TABLE sample_e2e.curated.e2e_engineer_check AS SELECT 1 AS id, 'SAMPLE' AS note;  -- works: CREATE_TABLE
%sql SELECT * FROM sample_e2e.restricted.salaries;                                -- must FAIL: permission denied
%sql CREATE TABLE sample_e2e.restricted.e2e_engineer_check AS SELECT 1 AS id;    -- must FAIL: no rights on restricted
```
Also expected for this joiner: **SQL warehouses and the SQL editor are not offered**, because the Engineer level has no Databricks SQL access. If they are offered, the joiner has the entitlement from another group: check Settings → Identity and access → Users → the joiner → Entitlements.

**C4. Admin view**: `bash $A status --account-id "$ACC" --workspace "$WS"`; account console → User management → Users → the joiner **Active**; optionally the AIM audit query in `docs/DATABRICKS_AIM.md`.

## D. Second joiner (proves "per joiner = Entra only")

Repeat B with another person for either group. **Do nothing in Databricks.** C2–C3 must pass for them too.

## E. Offboarding (removal in both planes)

1. PMO → the request → *Offboard user* → check the plan → tick → *Confirm*. The agent revokes the sessions, removes the TAP and the group membership, disables the account and, with deletion on, soft-deletes it (restorable 30 days).
2. Control plane: C1 shows the membership gone and the account disabled or deleted.
3. Data plane: the joiner can no longer sign in. Databricks does **not** remove the Databricks user or its personal access tokens: an admin deletes the user in the account console and revokes its tokens (the offboarding plan lists *Databricks workspace users* under *Not covered*). A table the engineer created stays, owned by that Databricks user: drop it or change its owner (clean-up below).

## Clean-up

- Stop the warehouse and terminate the cluster.
- As the catalog owner: `DROP TABLE IF EXISTS sample_e2e.curated.e2e_engineer_check;` then re-run A2b to reset `orders`.
- Keep `sample_e2e` and both groups for repeat runs, or drop the catalog (`DROP CATALOG sample_e2e CASCADE`) and remove both mapping rows (*Deprecate*).
