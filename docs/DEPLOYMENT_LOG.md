# Deployment Log — zero-cost pilot

| Date (UTC) | Step | Evidence | By |
|---|---|---|---|
| 2026-10-16 | Pre-check (`check_free_tier.sh`) | No existing Cosmos free-tier account; 0 Free-plan SWAs; caller role Owner | Gokul (Cloud Shell) |
| 2026-10-16 | What-if (`deploy_free.sh whatif`, subscription scope) | 13 creates: RG `rg-pmo-review-pilot` (centralindia), policy `pmo-pilot-allowed-types`, budget `pmo-pilot-zero-cost` (1, monthly, alerts >1% actual/forecast), Cosmos `cosmos-pmo-pilot-pkt2ofpqmlf3e` (enableFreeTier true, totalThroughputLimit 1000, single region, no zone redundancy, no analytical store), DB `pmoreview` (1000 RU/s shared) + 7 containers, SWA `swa-pmo-pilot-pkt2ofpqmlf3e` (Free, eastasia). No paid resource types. Reviewed by Claude against Bicep and code schema. | Gokul / Claude |
| 2026-10-16 | **Approval** | Gokul replied "approved" to the what-if above | Gokul |
| — | Apply (`deploy_free.sh apply`) | pending — paste output | — |
| — | Live synthetic request | pending | — |
| — | Cost check (deploy day, +24–48 h) | pending | — |
