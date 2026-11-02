# Permissions — planned (status filled in from evidence after each wave)

| Principal | Right | Scope | Why | Doc | Status |
|---|---|---|---|---|---|
| Workspace identity (SP) | Storage Blob Delegator | Storage account | User-delegation for shortcuts with container-level data access | FB3 | PLANNED |
| Workspace identity (SP) | Storage Blob Data Contributor | Container | Read + write for the shortcut and pipeline over TWA | FB3 | PLANNED |
| Workspace identity (SP) | Contributor (workspace role) | Fabric workspace | TWA prerequisite | FB3 | PLANNED |
| Owner (you) | Storage Blob Data Contributor (if no data role yet) | Container | Upload test files; Entra-auth portal browsing; notebook passthrough write | FB15 | PLANNED |
| Owner (you) | Workspace Admin | Fabric workspace | Create the identity and managed PE; run notebooks and pipelines | FB8, FB10 | PLANNED |
| Owner (you) | Capacity admin | F2 capacity | Assign the workspace; pause and resume | FB18 | PLANNED |
| Owner (you) | Network Contributor (or `subnets/join/action`) | `pvdbx-` VNet | Subnet delegation; gateway creation | FB12 | PLANNED (Owner already includes it) |
| Owner (you) | Subscription owner | Subscription | Register `Microsoft.PowerPlatform` / `Microsoft.Network` | FB10, FB12 | PLANNED |
| Fabric workspace | Resource instance rule | Storage firewall | Admits only this workspace on the public endpoint | FB4 | PLANNED |
