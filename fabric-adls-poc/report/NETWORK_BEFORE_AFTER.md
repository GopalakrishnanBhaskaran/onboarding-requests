# Network / firewall: before and after

> **Superseded 2026-11-03 (FB33):** this table is the tenant-1 POC set (`rg-pvdbx-fabric-poc`). The POC now uses storage account `pesstddevdaiweu01` in tenant 2. A new table follows after discovery there. The tenant-1 resources still exist until the RG is deleted (COST_LIMITS_CLEANUP.md §3).

There was no `pvdbx-` network to reuse (FB23), so **before** means the subscription before the POC, and **after** fills in as each wave lands. Each cell names its evidence file.

| Setting | Before POC | After base (23:49Z) | After Agent A (23:57Z) | After Agent B | After Agent C/D | Evidence |
|---|---|---|---|---|---|---|
| Resource group | none | `rg-pvdbx-fabric-poc` + ₹1,000 budget (50/80/100%) | same | | | W1-base-apply |
| VNet / subnets | none | `pvdbx-vnet-fabricpoc` 10.60.0.0/24, `snet-pe` /27 | same | | + gateway subnet (delegated) | W1-base-apply |
| Storage account | none | `stpvdbxfabb5731f`, HNS, LRS, TLS1.2, shared key off, blob public access off | same | | | W1-base-apply |
| Public network access | n/a | Enabled from selected networks | same | same | same | W1-base-apply, A-apply-owner |
| Default action | n/a | **Deny** | Deny | | | same |
| Trusted-services bypass | n/a | **None** | None | | | same |
| IP rules | n/a | none | **owner device IP (bare IP)** | | | A-apply-owner |
| Resource instance rules | n/a | none | none | *(planned: Microsoft.Fabric/workspaces/<ws id>)* | | |
| Private endpoints | none | none | **blob + dfs in `snet-pe`, Approved** | | + Fabric managed PE (dfs) *(planned)* | A-apply-owner |
| Private DNS | none | none | **privatelink.blob / privatelink.dfs**, linked, A records | | | A-apply-owner |
| Public DNS (seen outside) | n/a | `<acct>.dfs.core.windows.net` → public front end | → via `<acct>.privatelink.dfs.core.windows.net` | | | PROBE-01, PROBE-02 |
| Anonymous request from a non-allowed public IP | n/a | blob **403 AuthorizationFailure** | blob **403 AuthorizationFailure** | | | PROBE-01, PROBE-02 |
| Owner device: portal / data access | n/a | blocked (no IP rule) | *(to test in wave 3: Storage browser with Entra auth)* | | | |

Note: an anonymous **dfs** request returns `401 NoAuthenticationInformation` before the firewall check. Only the blob result proves the firewall block (PROBE-01).

---

# Tenant 2: `pesstddevdaiweu01` (current POC target)

Before = discovery 2026-11-03 01:22Z (FB34). The planned column shows the wave 1/2 plan. It isn't applied until approved, and its cells fill in from apply evidence.

| Setting | Before POC | Planned (wave 1 / 2) | After | Evidence |
|---|---|---|---|---|
| Public network access | **Disabled** | **kept Disabled** (TWA supports it, FB32) | | discovery, A-verify-existing |
| Default action / bypass | Deny / AzureServices | unchanged | | discovery |
| IP rules | none | none (no effect while Disabled) | | discovery |
| VNet rules | none | unchanged | | discovery |
| Resource instance rules | Defender for Storage data scanner | **+ Microsoft.Fabric/workspaces/<POC workspace id>** (wave 1) | | |
| Private endpoints (customer) | blob + dfs in `pes-vnet-data-dev-weu-001/pes-snet-data-dev-pep-weu-001`, Approved | reused, unchanged | | discovery |
| Managed PEs (Fabric) | dfs + blob from another workspace, Approved | **+ dfs from the POC workspace** (wave 2; only if that workspace has none) | | |
| Private DNS | zones not visible to the owner (hub, UNVERIFIED) | unchanged, no new zone | | discovery |
| VNet data gateway | exists on the dev VNet (`pes-snet-data-dev-vnetdgw-weu-001`) | reused (no subnet, provider or gateway change) | | discovery |
| Containers | `adlspdp-dev` (real data) | **+ `fabric-poc`** (empty, POC only); `adlspdp-dev` untouched | | |
| Anonymous request from another public IP | blob **403 AuthorizationFailure** | same expected after | | PROBE-T2-01 |
