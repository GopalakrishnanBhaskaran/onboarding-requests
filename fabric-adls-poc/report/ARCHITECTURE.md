# Architecture — Fabric (public) → ADLS Gen2 (private)

**Status: DESIGN. Not yet proven in the POC.** Each path is marked PASS or FAIL in `TEST_RESULTS.md` after wave 3.

```
                 Microsoft Fabric (SaaS, public)                     Your Azure subscription
 ┌───────────────────────────────────────────────┐          ┌──────────────────────────────────────────┐
 │ Workspace (F2 capacity) + workspace identity  │          │ ADLS Gen2 (HNS)                          │
 │                                               │ ①  TWA   │  public endpoint: selected networks      │
 │  Lakehouse ── OneLake shortcut ───────────────┼─────────►│   • default Deny                         │
 │  Pipeline (TWA connection) ───────────────────┼─────────►│   • resource instance rule:              │
 │                                               │          │     Microsoft.Fabric/workspaces/<ws id>  │
 │                                               │          │   • device IP (bare IP)                  │
 │  Notebook / Spark ── managed VNet ── MPE (dfs)┼──②──────►│  private endpoint conn. (Fabric-managed) │
 │                                               │          │                                          │
 │  Pipeline (gateway connection) ── VNet data   │          │  pvdbx- VNet                             │
 │  gateway (injected) ──────────────────────────┼──③──────►│   ├─ PE subnet: pe-blob, pe-dfs ──► ADLS │
 └───────────────────────────────────────────────┘          │   ├─ gateway subnet (delegated to        │
                                                            │   │  Microsoft.PowerPlatform/vnetaccess…)│
   Other public IPs ──────────────────────────────── ✗ 403  │   └─ private DNS privatelink.blob / .dfs │
   Your device (allowed IP) ─ portal / az ─────────── ✓     └──────────────────────────────────────────┘
```

| Path | Used by | How the storage firewall admits it | Identity | Facts |
|---|---|---|---|---|
| ① Trusted workspace access | OneLake shortcut, pipeline (Copy) | Resource instance rule for the workspace (public endpoint, default Deny) | Workspace identity | FB1–FB7 |
| ② Managed private endpoint | Spark notebooks, Spark job definitions | Private endpoint connection approved on the storage account | User running the notebook (Entra passthrough) | FB10, FB11 |
| ③ VNet data gateway | Pipelines, Dataflow Gen2, semantic models | Gateway in a delegated `pvdbx-` subnet → existing storage PE → private DNS | Org account (UI) or service principal; not workspace identity | FB8, FB12–FB14 |

Design choices:
- **Selected networks, not Disabled.** Both work for TWA (FB2). Selected networks keeps your device IP able to use the portal and `az`.
- **No trusted-services exception for Fabric.** The resource instance rule limits access to one workspace (FB5).
- **Least privilege.** The workspace identity gets Storage Blob Delegator (account) and Storage Blob Data Contributor (container only) (FB3).
