# Fabric → private ADLS Gen2: setup runbook (proven steps only)

**Status 2026-11-06.** Every step here passed in the POC on `pesstddevatacadaolzweu01` (PEST dev, public access Disabled) from workspace `pes-fab-data-dev-local-weu-1` (F16, West Europe). Evidence: `TEST_RESULTS.md`. Steps that were not run or are not possible are listed at the end, not in the procedure.

## What this setup gives
Spark notebooks and Spark job definitions in one Fabric workspace read and write the storage account over a Fabric **managed private endpoint**. The storage account keeps public network access **Disabled**. Nothing on the internet can reach it, including signed-in users with data roles.

## Prerequisites
- A Fabric workspace on an F SKU capacity, in a region with Fabric Data Engineering (MPEs need it in the tenant home region and the capacity region, FB11). You are workspace Admin.
- The storage account has HNS (ADLS Gen2), in the same tenant as the workspace.
- The people who run notebooks hold **Storage Blob Data Contributor** (or Reader for read-only) on the container. The notebook uses the caller's Entra identity.
- Someone who can approve private endpoint connections on the storage account (infra team).

## Procedure
1. **Create the managed private endpoints** (workspace Admin)
   Fabric → workspace → Workspace settings → **Network security** → Managed private endpoints → Create:
   - resource id of the storage account, sub-resource **dfs** (for `abfss://`), and a second one for **blob** if code uses blob URLs;
   - a request message that names the workspace and the account.
   The first MPE creates a managed VNet for the workspace. From then on starter pools are off and Spark sessions start in about 3–5 minutes (FB11). Tell the workspace users.
2. **Approve them on the storage account** (infra team)
   Storage account → Networking → **Private endpoints** → select the Pending Fabric connections → **Approve**.
3. **Confirm in Fabric**: Workspace settings → Network security → both MPEs **Succeeded / Approved**.
4. **Give data access**: Storage Blob Data Contributor to the notebook users (or their group) on the target container only.
5. **Create the container** if needed (ARM call; works while public access is Disabled):
   `az storage container-rm create --storage-account <storage account resource id> -n <container> --public-access off`
6. **Use it from a notebook** with the DFS path:
   ```python
   base = "abfss://<container>@<account>.dfs.core.windows.net/<folder>"
   df = spark.read.option("header", True).csv(base + "/input.csv")
   df.write.mode("overwrite").parquet(base + "/output")
   ```
   To make the data visible to the SQL analytics endpoint or Power BI, load it into a lakehouse table:
   `df.write.mode("overwrite").format("delta").saveAsTable("<table>")`

## Checks (repeat after any network change)
| Check | Command | Expected |
|---|---|---|
| Firewall unchanged | `az storage account show -n <account> --query "{public:publicNetworkAccess,default:networkRuleSet.defaultAction}"` | Disabled / Deny |
| MPEs approved | `az network private-endpoint-connection list --id <account id> -o table` | Fabric connections Approved |
| Internet blocked | `bash fabric-adls-poc/tools/e_tests.sh probe` (from Cloud Shell) | blob 403 `AuthorizationFailure`, anonymous and signed in |
| Fabric read/write | `bash fabric-adls-poc/tools/e_tests.sh notebook` | `NOTEBOOK PASS read 3 rows + wrote/read back parquet` |

## Not in this runbook (and why)
| Item | Status | What it needs |
|---|---|---|
| OneLake shortcut to the account | Not possible today | A **resource instance rule** for the workspace on the storage account (trusted workspace access) plus a Blob data role at account scope (or Delegator + ACLs) for the shortcut identity. Shortcuts don't use managed private endpoints, and gateway shortcuts exist only for S3, S3-compatible and GCS (FB39). |
| Pipeline copy over trusted workspace access | Not possible today | The same resource instance rule, and a data role at storage-account scope (FB6, FB32) |
| Pipeline copy over the VNet data gateway | Not run | A gateway whose VNet reaches the account's private endpoint and private DNS (UNVERIFIED for this account), a gateway connection with an organizational account or service principal (FB8) |

## Rollback
- Delete the test objects: lakehouse `poc_lh`, notebook `poc_nb_mpe`, container `fabric-poc` (`az storage container-rm delete --storage-account <id> -n fabric-poc --yes`).
- Remove the managed PEs: Workspace settings → Network security → Delete (wait 15 minutes before re-creating one to the same resource). Infra can also reject the connection on the storage side.
- Remove role assignments made for the POC.
