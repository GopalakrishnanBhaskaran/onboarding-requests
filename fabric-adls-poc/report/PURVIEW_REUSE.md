# Reusing the POC storage private endpoints for Purview

> **Superseded 2026-11-03 (FB33):** the PE details below are from the tenant-1 POC set. For the Purview work in tenant 2, re-run `tools/pe_details.sh` there after discovery.

**Status (2026-11-02 23:57Z):** the storage private endpoints exist and are approved (Agent A apply, 9 of 9 changes). Get the live values (ids, private IPs, DNS zone ids) with the read-only script below and pass its screen output to the Purview team. Don't commit the unmasked output.

```bash
bash fabric-adls-poc/tools/pe_details.sh
```

## What exists (shareable summary)
| Item | Value |
|---|---|
| Storage account | `stpvdbxfabb5731f`: ADLS Gen2 (HNS), Central India, Entra auth only (shared key off) |
| Firewall | Public network access *Enabled from selected networks*, default **Deny**, bypass **None**. Allowed: the owner's device IP; later the Fabric workspace resource instance rule |
| VNet | `pvdbx-vnet-fabricpoc` 10.60.0.0/24 in `rg-pvdbx-fabric-poc`; PE subnet `snet-pe` 10.60.0.0/27 |
| Private endpoints | `pe-stpvdbxfabb5731f-blob` (group `blob`), `pe-stpvdbxfabb5731f-dfs` (group `dfs`), both Approved |
| Private DNS | `privatelink.blob.core.windows.net`, `privatelink.dfs.core.windows.net` in `rg-pvdbx-fabric-poc`, linked to `pvdbx-vnet-fabricpoc`, A records for the account |
| Proof | Public DNS CNAMEs now go through `stpvdbxfabb5731f.privatelink.*`. An anonymous blob request from a non-allowed public IP returns `403 AuthorizationFailure` (evidence/PROBE-02) |

## How Purview can use them (Microsoft Learn: [Purview network best practices](https://learn.microsoft.com/en-us/purview/concept-best-practices-network), updated 2025-10-10)
| Purview scan option | Reuses these storage PEs? | What it needs on this storage account | Notes from the docs |
|---|---|---|---|
| **Self-hosted integration runtime (SHIR)** on a Windows VM in `pvdbx-vnet-fabricpoc` or a peered VNet | **Yes.** The VM resolves `stpvdbxfabb5731f.dfs/blob.core.windows.net` to the PE IPs through the linked private DNS zones (link them to the SHIR's VNet if it's peered) | A data role for the scan identity (Storage Blob Data Reader). No firewall change | "Currently, you can't use a Microsoft Purview managed identity with a self-hosted integration runtime." Use a service principal; account key is off here. The credential lives in Key Vault. A VM is a new cost |
| **Managed VNet integration runtime** | **No.** Purview creates its own managed PE | Approve Purview's pending PE connection on the storage account (same step as Fabric's, Agent C) + data role | "a managed private endpoint must be deployed within Microsoft Purview Managed Virtual Network, even if the data source already has a private network in your Azure subscription" |
| **Azure integration runtime** (public endpoint) | No | Would need the trusted-services bypass or a resource instance rule for the Purview account | Bypass is **None** on purpose: with it on, any Fabric workspace with an identity in the tenant could get in, which spoils the POC's proof (FB5). A Purview resource instance rule is **UNVERIFIED**; check it before you use it |
| Purview's **own** account / portal / ingestion PEs | n/a | n/a | They're separate PEs for the Purview account itself and can go in the same VNet (`snet-pe` has room: /27) |

**Recommendation:** Managed VNet IR (least admin, no VM) is the docs' preferred option. It needs one approval on this storage account, as Fabric does. Use a SHIR in this VNet only if the Purview team must reuse the existing PEs; it reuses PEs and DNS as they are.

Any change on this storage account for Purview (PE approval, role, rule) is a network or role change. It needs its own "approved" first.
