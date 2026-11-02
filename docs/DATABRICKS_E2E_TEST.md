# Databricks end-to-end test: one joiner, control plane and data plane

Written 1 Nov 2026, automated 2 Nov 2026. Facts: F106, F110–F118 (`docs/VERIFIED_FACTS.md`). Tools: `tools/databricks/aim_setup.sh` (`docs/DATABRICKS_AIM.md`).
Log every result in `docs/DEPLOYMENT_LOG.md` as **live check V**. About 1–1.5 hours the first time.

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
| Data: compute | SQL warehouse **Can use** for the group | workspace admin | once per group |
| Data: the user | created in Databricks on first sign-in, status Active | AIM, automatic | every joiner |

## Rules this test respects

1. **One row per plane.** A `dbx:` row carries **no Azure role**. If it does, the agent leaves the request to the role assignment team (*one row per plane*), so a request is never completed with half of its access in place.
2. **The group exists before the request.** A group still to be created resolves as *Unresolved* on the request form and cannot be requested.

## Prerequisites (check before starting)

- [ ] **A Databricks workspace**: Premium, Unity Catalog, identity-federated, in the **agent's tenant** (pilot: `dbw-pmo-pkt2ofpqmlf3e`; its approval and cost line are still to be recorded, `DEPLOYMENT_LOG.md` 2 Nov 2026).
- [ ] **People**: a Databricks account admin, a workspace admin and a catalog owner (can be one person). In the app: the Gmail tester (Requester, Owner, RoleTeam, IdTeam, PMO) and the pilot admin (Security).
- [ ] **AIM on**: `bash tools/databricks/aim_setup.sh plan --account-id "$ACC"`. If it is off: the console toggle (`docs/DATABRICKS_AIM.md`).
- [ ] **The new code is deployed**: `verify_features` line 1 PASS.

## A. One-time setup for the test group

**A1. Entra group** (Entra admin center → Groups → New group): Security, name `azr-pes-rg-data-dev-dbw-weu-datareaders` (it matches the PEST naming rule `azr-pes-rg-data-(dev|tst|ppd|prd)-<x>-weu-<name>`; `datareaders` makes it a **Reader**). Copy its **object id**.

**A2. Mapping row** (PMO in the app → *Mapping sheet* → *Add a row*, tenant PEST):

| Field | Value |
|---|---|
| Resource | `Databricks PEST Dev` |
| Access level | `Data Reader` |
| Entra group | `azr-pes-rg-data-dev-dbw-weu-datareaders` |
| Role | `Databricks workspace user` |
| Role scope | `dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated` (workspace / catalog . schema; without `.schema` the whole catalog) |
| Source reference | `E2E test 2026-11-02` |
| Privileged / Sensitive / Group to be created | not ticked |

Then **Edit** the row and paste the **Group object id** from A1. Leave the *Azure roles* box empty (rule 1).

**A2b. Test data** (SQL editor, once, as the catalog owner or a metastore admin; SAMPLE data only):
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
ACC='<Databricks account id>'; WS=dbw-pmo-pkt2ofpqmlf3e; G='azr-pes-rg-data-dev-dbw-weu-datareaders'
A=tools/databricks/aim_setup.sh
az ad group show --group "$G" --query "{name:displayName, id:id}" -o table          # A1 must exist
bash $A plan  --account-id "$ACC" --workspace "$WS" --group "$G" --grant sample_e2e.curated
bash $A apply --account-id "$ACC" --workspace "$WS" --group "$G" --grant sample_e2e.curated --approved-by "Gokul 2026-11-02"
```
Expect `PASS DBX_WORKSPACE`, `PASS DBX_ENTITLEMENTS`, `PASS DBX_UC_CATALOG` and `PASS DBX_UC_SCHEMA`, followed by a log line ready for `DEPLOYMENT_LOG.md`. Any FAIL exits non-zero.

*Way 2, the agent does it for every request* (security change: dry run, then "approved"):
```bash
tools/deploy_free.sh databricks $SUB on --account-id "$ACC" --workspace dbw-pmo-pkt2ofpqmlf3e --catalogs sample_e2e
```
The dry run prints the worker identity's client id and the **Databricks-side rights** an account admin gives it once (F118):
1. Service principal (Entra ID managed).
2. **Account admin** (needed for A3).
3. **Admin** on the workspace (needed for A4).
4. `USE CATALOG` and `MANAGE` on `sample_e2e` (needed for A5).

Then run the same command with `--approved-by "Gokul <date>"`, run `tools/deploy_free.sh app $SUB` (the website must follow the switch), and check `verify_features` line 18: PASS. With Way 2, step B5 needs no person: the agent sets up and tests the group.

**A6. Compute**: SQL warehouses → the smallest warehouse with the shortest auto-stop → Permissions → the group: **Can use**. It costs DBUs only while running; stop it after the test.

## B. The joiner, through the app

1. **Requester** (Gmail tester) → *New request*: tenant PEST, Enterprise ID (a test mail address), first and last name, environment **Dev**, resource **Databricks PEST Dev**, tick the group, *Insert into justification* and fill it in (a reader level needs a shorter reason than write or admin levels), Owner = the Gmail tester's sign-in name → *Submit request*. Expect *Awaiting Owner* within seconds (or after the worker's Entra check, within 5 minutes).
2. **Owner** (Gmail tester) → *My actions* → *Open to decide* → tick → *Approve business need*.
3. **Security** (pilot admin) → *My actions* → *Approve risk and access level*.
4. **AI agent** (within about 5 minutes): creates the user `<first.last>@gopalakrishnanbaskarangmail.onmicrosoft.com` and adds it to the group. No TAP sender mailbox is set, so the **ID team** issues the one-time pass: Entra admin center → the user → Authentication methods → Add → Temporary Access Pass (one-time). Then in the app: *Pick up task*, record only the issued time, expiry and one-time flag → *Record completion*. Never type the pass into the app, mail or chat.
5. **Role assignment step.** *Way 2:* the agent sets the group up in Databricks and tests it (the trail shows `agent-databricks-setup` and `agent-databricks-tests`). *Way 1:* the task says the agent left it; keep the prefilled line `Databricks workspace user | dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated` → *Record completion*. With Way 2 switched on, the verification then tests the group live in Databricks (DBX_* checks on the role task).
6. **AI verification** → **Completed**; one completion mail (redirected to the test mailbox).

## C. Evidence

**C1. Control plane** (Cloud Shell):
```bash
UPN='<new user principal name>'; G='azr-pes-rg-data-dev-dbw-weu-datareaders'
az ad user show --id "$UPN" --query "{upn:userPrincipalName, enabled:accountEnabled}" -o table
az ad group member check --group "$G" --member-id "$(az ad user show --id "$UPN" --query id -o tsv)" -o table   # value: true
```
In the app: the request page shows the journey at *Verified* and the final verification checks.

**C2. First sign-in** (InPrivate window): open `https://adb-<id>.<n>.azuredatabricks.net`, sign in with the UPN and the one-time pass, register MFA. Expect to land in the workspace. If you see an entitlement error, A4 is missing (`DBX_ENTITLEMENTS`).

**C3. Data access as the joiner** (SQL editor, the A6 warehouse):
```sql
SELECT current_user();                                                   -- the joiner's UPN
SELECT is_account_group_member('azr-pes-rg-data-dev-dbw-weu-datareaders'); -- true
SELECT * FROM sample_e2e.curated.orders;                                 -- 1 row
SELECT * FROM sample_e2e.restricted.salaries;                            -- must FAIL: permission denied
```

**C4. Admin view**: `bash $A status --account-id "$ACC" --workspace "$WS"`; account console → User management → Users → the joiner **Active**; optionally the AIM audit query in `docs/DATABRICKS_AIM.md`.

## D. Second joiner (proves "per joiner = Entra only")

Repeat B with another person. **Do nothing in Databricks.** C2–C3 must pass for them too.

## E. Offboarding (removal in both planes)

1. PMO → the request → *Offboard user* → check the plan → tick → *Confirm*. The agent revokes the sessions, removes the TAP and the group membership, disables the account and, with deletion on, soft-deletes it (restorable 30 days).
2. Control plane: C1 shows the membership gone and the account disabled or deleted.
3. Data plane: the joiner can no longer sign in. Databricks does **not** remove the Databricks user or its personal access tokens: an admin deletes the user in the account console and revokes its tokens (the offboarding plan lists *Databricks workspace users* under *Not covered*).

## Clean-up

Stop the warehouse. Keep `sample_e2e` and the group for repeat runs, or drop the catalog (`DROP CATALOG sample_e2e CASCADE`) and remove the mapping row (*Deprecate*).
