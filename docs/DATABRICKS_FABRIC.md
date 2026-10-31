# Databricks and Fabric with Entra groups (31 Oct 2026)

**Status: coded, not deployed.** Every Azure resource or permission change below needs the dry run first and an explicit "approved" (CLAUDE.md rule 4). Facts: `docs/VERIFIED_FACTS.md` F71–F79.

## How it fits the onboarding flow
```
Request → AI review → Owner → Security → agent / ID team adds the joiner to the Entra group → AI verification
                                                     │
                     ┌───────────────────────────────┴───────────────────────────────┐
   Databricks: the group is on the workspace (set once).        Fabric: the group has a workspace role (set once).
   Automatic identity management creates the user at the         Fabric reads Entra directly.
   first sign-in and syncs the membership from Entra.
```
After the one-time set-up, a joiner needs **only the Entra group membership**: nothing per joiner in Databricks or Fabric, no SCIM app, no sync job. Removing the joiner from the group removes the access. The agent gets no Databricks or Fabric rights.

## Part A: Databricks (one serverless workspace)

**Why serverless.** A classic workspace (`computeMode: Hybrid`) needs a managed resource group with a VNet and a DBFS storage account, which CLAUDE.md rule 2 does not allow. A serverless workspace creates **only the workspace resource**: its compute and default storage run in the Databricks account (F71). Central India supports it (F72). It runs on the Premium tier (identity federation, Unity Catalog, automatic identity management).

**Cost.** Nothing while idle. DBUs are charged only while a notebook, job or SQL warehouse runs, and they count against the existing ₹2,000 budget. The DBU rates and default-storage billing are **not checked yet** (F76): read the Azure Databricks pricing page before you approve. Stop any SQL warehouse after each test, then run `tools/cost_check.sh`.

| Step | Who | Command / action |
|---|---|---|
| A1 Dry run | Operator | `tools/deploy_free.sh params $SUB` (after a Cloud Shell reset), then `tools/deploy_free.sh databricks $SUB`. This shows the plan, the provider state and the what-if (written to `.local/whatif-databricks.txt`). Send it to Gokul. |
| A2 Create | Operator, after "approved" | `tools/deploy_free.sh databricks $SUB --approved-by "Gokul <date>"`. It registers `Microsoft.Databricks` if needed, allows `Microsoft.Databricks/workspaces` in the policy, waits for the policy (up to 30 min), then creates `dbw-pmo-<suffix>` and prints its URL. |
| A3 Admins (once) | Pilot admin (member account, not the Gmail guest) | 1. Open the workspace URL as the deploying admin (Owner on the subscription → workspace admin). 2. As an **Entra Global Administrator**, sign in at https://accounts.azuredatabricks.net. This sign-in makes you the first account admin (F77). 3. Copy the **account id** from the user menu (top right). 4. Check that **Security → Identity provider setup → Automatic identity management** shows Enabled. It is on by default for accounts created after 1 Aug 2025 (F73). |
| A4 Groups dry run | Account admin | `python3 tools/databricks_setup.py --account-id <id> --group PH-DATAENGINEERS-DEV --group PH-SUPPORT-READER-DEV`. Read-only: it shows the workspace, the current assignments and the plan. Send the output for approval. |
| A5 Groups apply | Account admin, after "approved" | Same command `--approved-by "Gokul <date>"`. Each group is resolved from Entra and assigned to the workspace as **USER**, then read back (`PASS`/`FAIL`). It gives ADMIN only for a group passed as `NAME=ADMIN` (privileged), and it never lowers or removes a permission. |
| A6 Data access (role team) | Workspace admin | Workspace access is not data access. Grant Unity Catalog privileges to the group once, for example in the SQL editor: ``GRANT USE CATALOG ON CATALOG <catalog> TO `PH-SUPPORT-READER-DEV`;`` and ``GRANT USE SCHEMA, SELECT ON SCHEMA <catalog>.<schema> TO `PH-SUPPORT-READER-DEV`;``. Writers also get `MODIFY`, `CREATE TABLE` on their schema. |

## Part B: Fabric (Entra groups on a workspace)

Fabric needs **no Azure resource** for the test: use the free 60-day trial capacity (F79). A paid F capacity (`Microsoft.Fabric/capacities`) would be a new paid resource and needs Gokul's written approval.

| Step | Who | Command / action |
|---|---|---|
| B1 Tenant settings | Fabric admin (Global Admin) | https://app.fabric.microsoft.com → **Admin portal → Tenant settings**: turn on *Users can create Fabric items* (optionally limited to a security group) and *Users can try Microsoft Fabric paid features*. |
| B2 Trial | Pilot admin | Account manager (photo, top right) → **Start trial** → choose a region (your home region, or Central India if listed) → **Activate**. You become capacity admin. |
| B3 Workspace | Pilot admin | **Workspaces → New workspace** `PMO Fabric test` → **Workspace settings → Workspace type → Fabric Trial**. Optionally add a lakehouse so testers have something to open. |
| B4 Groups | Pilot admin (workspace Admin or Member) | Dry run: `python3 tools/fabric_groups.py --workspace "PMO Fabric test" --group PH-SUPPORT-READER-DEV --group PH-DATAENGINEERS-DEV=Contributor`. After "approved": add `--approved-by "Gokul <date>"`. Default role is Viewer; Contributor and Member are allowed; the tool never gives Admin and never changes an existing role. By hand: workspace → **Manage access → Add people or groups**. |

Each tester needs a Fabric licence. The free licence is enough: the user signs up at the first sign-in to app.fabric.microsoft.com, or an admin assigns it. Use member accounts of the tenant for testers. Guest access needs extra tenant settings that this pilot has not checked.

## Test plan (both platforms)
1. **Before.** The test user is in no group. The Databricks workspace URL refuses them, and Fabric does not show the workspace.
2. **Onboarding request.** Submit a request for the test user on the group's mapping row. The Owner approves, then Security. The agent or the ID team adds the membership. Step 7 verification shows PASS.
3. **Access.** In a private window, sign in as the test user:
   - Databricks: the workspace opens, and the user appears as *External / Active* in account console → User management.
   - Fabric: the workspace shows with the group's role.
4. **Removal.** Remove the user from the group in Entra.
   - Databricks: access goes at the next membership refresh (browser sign-in more than 5 minutes after the last sync, F75).
   - Fabric: sign out and in again (the timing has not been checked).
5. **Evidence (Databricks).** Rerun the A4 dry run: it shows `keep USER`. If system tables are on, the audit log shows what automatic identity management did. Running the query needs a SQL warehouse, which costs DBUs:
   ```sql
   SELECT event_time, action_name, request_params.targetUserName, request_params.targetGroupName
   FROM system.access.audit
   WHERE request_params.endpoint = 'autoUserCreation'
      OR request_params.groupMembershipType = 'IdentityProvider'
   ORDER BY event_time DESC;
   ```
Log each step in `docs/DEPLOYMENT_LOG.md` with its output.

## Not done / open
- Databricks and Fabric rows still go to the **role assignment team**, because they carry no Azure role. Once the group is set up, the team only confirms. Possible later change: rows marked "granted by group" complete step 6 when the membership exists. That needs code and a design decision.
- Step 7 verifies the Entra account, the membership and the Azure roles. It does not verify a Databricks or Fabric sign-in, by design (22 Oct 2026).
- The Azure CLI token for the Fabric API is confirmed only by search results (F78). Check it on the first run.
- Production tenants (PEST, Vestacy) would need the same set-up in their own tenants. Automatic identity management does not work across tenants (F73).
