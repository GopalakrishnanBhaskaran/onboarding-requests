# PERMISSIONS — v1.1 (P3 checkpoint, 30 Oct 2026)

**Approved as proposed, 30 Oct 2026 (Gokul; decisions 16–19 in `docs/V1_1_PLAN.md`):** app role assignments stay "Not covered" (no `Directory.Read.All`, F78); RBAC reach = `OFFBOARD_SUBSCRIPTIONS` (the pilot subscription at first); TAP mail through Graph with `Application Mail.Send` scoped by Exchange RBAC for Applications. Approving the design is not a grant: each row below still needs its dry run and `--approved-by`.

Every Microsoft Graph and Azure (ARM) permission v1.1 needs, why, the narrowest option, and whether the worker already holds it. Facts: `docs/VERIFIED_FACTS.md` (F-numbers, each read on learn.microsoft.com). **Nothing here is granted yet.** Each grant is an Azure/Entra permission change: dry run → Gokul's "approved" → `--approved-by` (CLAUDE.md rule 4).

Who holds what: only the **AI agent worker's managed identity** (Flex Consumption Function App, `func-pmo-worker-*`). The Static Web Apps API has no managed identity (F85) and never calls Graph or ARM. People use their own rights in their own tools.

## 1. Held today (for reference)

| Permission | Type | Why (today) | Switch |
|---|---|---|---|
| `User.Read.All` | Graph application | find users (review, verification) — F45 | always |
| `GroupMember.Read.All` | Graph application | find groups, check membership — F45, F46 | always |
| `UserAuthenticationMethod.Read.All` | Graph application | read a user's methods / TAP metadata — F47, F79 | always |
| `AuditLog.Read.All` | Graph application | sign-in logs (F48) — **unused since 22 Oct**; removal is optional | — |
| `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All` | Graph application | v2.1 provisioning — F56–F58 | `AGENT_PROVISIONING` |
| `UserAuthMethod-TAP.ReadWrite.All` | Graph application | agent TAP create — F66 | `AGENT_TAP` |
| Role Based Access Control Administrator **with condition** (allow-listed roles, user/group principals) on one resource group | Azure RBAC | v2.1 role assignment — F59, F60 | `AGENT_ROLE_ASSIGNMENT` |
| Cosmos DB Built-in Data Contributor on the containers it uses; Reader on `projects` | Cosmos data plane | worker data (F31, F90) | always |
| A role on the Communication Services resource | Azure RBAC | ACS mail — F63 | `NOTIFY_SENDER=AcsEmail` |

## 2. New or reused for v1.1

### F1 Resource* selector, F3 Prod read-only, F4 uploads
| Permission | Type | Why | Narrowest option | New? |
|---|---|---|---|---|
| `GroupMember.Read.All` | Graph application | worker resolves mapping group names (`GET /groups?$filter=displayName eq …`, F45) | held | **No** |
| Cosmos data roles on `peopleInventory`, `escalations`, `mappingQualityIssues`, `groupResolutions`, `offboardingRuns`, `auditEvents` | Cosmos data plane | worker writes escalations, resolutions, quality issues, offboarding runs, audit; reads the people inventory | container scope (Bicep `writableContainers`), never `userAccess` or the upload versions | **Yes** (Bicep, approval with the what-if) |
| App role **`Uploads.Write`** on a new Entra app registration "PMO uploads API" (application type; manifest `requestedAccessTokenVersion: 2`, F99) | Entra app role | F4 external API: a client app gets it by admin consent; the token's `roles` claim carries it, `tid` must be the tenant, `aud` the API (F91, F99; `docs/EXTERNAL_UPLOAD_API.md`) | one app role, assigned per client app; no Graph permission at all | **Yes** — only when the external API is switched on (`EXTERNAL_UPLOAD_API_ENABLED=false` until then; reachability: `HOSTING.md`) |

### F2 TAP mail to the enterprise mail only
| Permission | Type | Why | Narrowest option | New? |
|---|---|---|---|---|
| `User.Read.All` | Graph application | cross-check the people inventory's EnterpriseEmail with the Entra user's `mail` / `userPrincipalName` / `userType` before sending (F95) | held | **No** |
| `UserAuthMethod-TAP.ReadWrite.All` | Graph application | create the one-time TAP (agent TAP, unchanged) | held; tenant-wide by design (F66), code limits it to the user the agent created | **No** |
| **`Application Mail.Send`** (Exchange Online **RBAC for Applications**) scoped to the one sender mailbox | Exchange role assignment | Graph `sendMail` from that mailbox (F52) | `New-ManagementScope -RecipientRestrictionFilter "PrimarySmtpAddress -eq '<sender>'"` + `New-ManagementRoleAssignment -App <worker app id> -Role "Application Mail.Send" -CustomResourceScope <scope>`; check with `Test-ServicePrincipalAuthorization` (F81). **Do not** grant `Mail.Send` in Entra as well: grants add up and the scope would have no effect (F81). | **Yes** — only if decision 3 stays Graph mail; alternative: keep ACS for the TAP mail (no new permission; the recipient checks are the same) |

### F5 Offboarding (worker only, `AGENT_OFFBOARDING=false` until approved)
| Step | Permission | Type | Why | Narrowest option | New? |
|---|---|---|---|---|---|
| 1 Resolve | `User.Read.All` | Graph application | user by Enterprise ID (UPN / mail), stop if 0 or > 1 (F92, F95) | held | No |
| 2 Plan: groups | `GroupMember.Read.All` | Graph application | `POST /users/{id}/getMemberGroups` (transitive, ≤ 11,000, F77), then a direct-member read per group (`GET /groups/{id}/members` — direct, not transitive; `$filter` only as an advanced query with `ConsistencyLevel: eventual`, F94; fallback: page through the members) | held. `GET /users/{id}/memberOf` would need **`Directory.Read.All`** (F76): not proposed | No |
| 2 Plan: app role assignments | `Directory.Read.All` | Graph application | `GET /users/{id}/appRoleAssignments` (F78) | **Not proposed:** broad tenant-wide read. Default: listed under "Not covered" (disabling the account stops sign-in to those apps anyway). Grant only if you want them listed. | Optional |
| 2 Plan: methods / TAP | `UserAuthenticationMethod.Read.All` | Graph application | list methods incl. TAP (F79) | held | No |
| 2 Plan: sessions | — | — | no session read is used (design); the plan says "all sessions are revoked", which `revokeSignInSessions` does (F71) | — | — |
| 2 Plan: Azure RBAC | `Microsoft.Authorization/roleAssignments/read` | Azure RBAC | `GET /subscriptions/{s}/providers/Microsoft.Authorization/roleAssignments?$filter=assignedTo('{id}')` per subscription (F82) | **Reader** at each subscription in scope (or a custom role with only `roleAssignments/read` and `subscriptions/read`); which subscriptions the identity sees is shown by the list call (F84, to prove live) | **Yes** |
| 3 Revoke sessions | **`User.RevokeSessions.All`** | Graph application | `POST /users/{id}/revokeSignInSessions` (F71; takes minutes; not for external users) | the only option | **Yes** |
| 3 Remove TAP | `UserAuthMethod-TAP.ReadWrite.All` (held) | Graph application | `DELETE …/temporaryAccessPassMethods/{id}` (F72) | Learn lists `UserAuthenticationMethod.ReadWrite.All` as least privileged and the TAP-only permission as higher; the TAP-only one is **narrower in effect** (TAP methods only) and already held → reuse it, prove live with the dry run | No |
| 3 Remove memberships | `GroupMember.ReadWrite.All` (held, v2.1) | Graph application | `DELETE /groups/{g}/members/{u}/$ref` — always with `/$ref` (without it the member object itself is deleted, F73) | held; role-assignable, dynamic and on-premises-synced groups cannot be changed → leftovers | No |
| 3 Remove Azure role assignments | `Microsoft.Authorization/roleAssignments/delete` | Azure RBAC | `DELETE {scope}/providers/Microsoft.Authorization/roleAssignments/{name}` (F83) | **Role Based Access Control Administrator with a delete condition**: `@Resource[Microsoft.Authorization/roleAssignments:PrincipalType] StringEqualsIgnoreCase 'User'` (users only, never groups or service principals; F60 pattern) at each subscription in scope. Code deletes only assignments whose principal is the user being offboarded; inherited assignments are removed at their own scope (F83) | **Yes** |
| 3 Disable | **`User.EnableDisableAccount.All`** + `User.Read.All` | Graph application | `PATCH /users/{id}` `accountEnabled=false` (F74: least privileged combination) | the only narrow option (not `User.ReadWrite.All`) | **Yes** |
| 3 Delete (off) | `User.ReadWrite.All` | Graph application | `DELETE /users/{id}`, restorable 30 days (F75) | **Not proposed** (`OFFBOARD_DELETE_USER=false`); grant only if you switch deletion on | Optional |
| 5 Verify | the read permissions above | — | re-read memberships, methods, RBAC, `accountEnabled` (F77, F79, F82, F95) | held / as above | — |
| 6 Notify PMO | ACS role (held) or `Application Mail.Send` (above) | — | one mail to the project's PMO list only (F63, F81) | as above | No |

Built (P5, 30 Oct 2026): reads in `GraphDirectoryReader` (getMemberGroups with `securityEnabledOnly: false`; an id that is not a group — e.g. a directory role — is a leftover, no extra permission, F102) and `ArmRoleAssignmentReader` (subscriptions F84, assignments F82); writes only in `GraphOffboardingWriter` and `ArmRoleAssignmentRemover`, created by the worker only with `AGENT_OFFBOARDING=true`. Dynamic and role-assignable groups are never attempted (F102). Without the RBAC grants a subscription is "could not be read" → leftover.

## 3. Summary of what Gokul approves at deploy time (each with its own dry run)

| # | Change | Needed for | Scope | Risk and limit |
|---|---|---|---|---|
| A | 6 Cosmos container data roles + 8 containers | v1.1 data | the database `pmoreview` | none beyond data; what-if shows it (F31; 21 of 25 containers under shared throughput, F90) |
| B | `User.RevokeSessions.All` (F71) | F5 | tenant-wide (Graph app permissions cannot be scoped to a group) | code calls it only for the confirmed offboarding user |
| C | `User.EnableDisableAccount.All` | F5 | tenant-wide | code calls it only for the confirmed offboarding user, who must be a person of the project (people inventory, or a request its Owner and Security approved) in a project that names the agent's own tenant, and held by no other project of that tenant; a user holding an Entra directory role (directly or through a role-assignable group; `getMemberGroups` with `securityEnabledOnly=false` returns directory roles, F77/F102 — no extra permission) is left to people: no Entra or Azure write is planned for that account (built P6, test `F5_A_directory_role_holder_is_left_to_people_and_nothing_is_changed_in_Entra_or_Azure`) |
| D | Reader (or custom read) + constrained RBAC Administrator (delete, users only) per subscription | F5 | named subscriptions only | could remove any user's assignment there: code limits to the offboarded user; Azure condition limits to users |
| E | Exchange `Application Mail.Send` scoped to the sender mailbox | F2 (if Graph mail) | one mailbox | needs an Exchange admin; never also in Entra |
| F | Entra app registration + `Uploads.Write` app role | F4 external API | per client app | only when the external API is switched on |
| — | `Directory.Read.All` (F78), `User.ReadWrite.All` (F75) | optional F5 extras | tenant-wide | **not proposed** |

Option 3 as chosen (decision F5a, 30 Oct 2026): **B and C only** (`deploy_free.sh offboarding <sub> on`, dry run → `--approved-by`). **D is deferred:** Learn documents conditions that limit which roles and principal types RBAC Administrator may assign or remove, but no documented way to remove its create right, so the agent gets no Azure right for now and Azure role assignments stay with the role assignment team (a leftover in every run).

Retired / unused that can be removed (optional, approval): `AuditLog.Read.All` (F48).

## 4. Admin consent
Graph application permissions need an administrator who can grant tenant-wide admin consent (e.g. Privileged Role Administrator or Global Administrator) — the same as for the v2.1 grants (`deploy_free.sh grant-graph` / `provisioning`). The Exchange scope needs an Exchange administrator. Azure role assignments need Owner or User Access Administrator / RBAC Administrator on the subscriptions in scope.
