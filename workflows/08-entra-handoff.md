# 08 — Entra hand-off (Phase 2, Option A)

**Purpose:** Take an accepted, ready review through to access in Microsoft Entra, without the app approving or assigning anything.

**Approved:** Gokul, 16 Oct 2026. Option A (Entra access packages), Entra approves, background worker syncs status, ₹2,000/month cap from Azure credits.

## Flow
1. **PMO** creates the request, including the new joiner's sign-in name (UPN), and clicks Analyze.
2. **Review** gives *Ready for human review*, with the role mapping *Matched*.
3. **Reviewer** records *Accepted*. The app creates a hand-off record with the access package's **My Access link**.
4. **PMO** sends the link to the new joiner, who requests the access package in My Access.
5. **Entra approver** approves or denies in My Access. Entra emails both people.
6. **Entra** adds the joiner to the group on the start date and removes them on the end date.
7. **Worker** reads Entra every 5 minutes. The request page shows each status: Awaiting request, Pending approval, Delivering, Delivered, then Expired. Denied, Cancelled and Failed are also shown.

The app holds only Graph `EntitlementManagement.Read.All`. It cannot approve, assign or remove access.

## One-off setup (Gokul, in this order)

| # | Where | Step |
|---|---|---|
| 1 | Entra admin center | Create a **cloud admin account**, for example `pilot.admin@<tenant>.onmicrosoft.com`, with the Global Administrator role. Gmail (Microsoft account) sign-ins cannot start licence trials. Sign in as this account for steps 2–6. |
| 2 | Entra admin center > Billing > Licenses > All products > **Try / Buy** | Start the free **Microsoft Entra ID P2** (or Entra ID Governance / Entra Suite) trial. Note the end date and cancel or let it lapse. Azure credits do not pay for Entra licences (F28). |
| 3 | Cloud Shell | `bash tools/setup_entra.sh identities <tenant>.onmicrosoft.com pilot.admin@<tenant>.onmicrosoft.com` creates group `SAMPLE-GRP-ALPHA-DEV-ENG` and test user `sample.joiner@…`, and assigns trial licences to the joiner and the approver. |
| 4 | Entra admin center > Identity Governance > Catalogs | Create catalog **PMO Pilot SAMPLE**, then add resource **Groups and Teams > SAMPLE-GRP-ALPHA-DEV-ENG**. |
| 5 | Identity Governance > Access packages > **New** | Name `SAMPLE Data Engineer – Dev`, catalog PMO Pilot SAMPLE, resource role **Member** of the group. **Policy:** For users in your directory, specific users = `sample.joiner` (or all members). Require approval = **Yes**, single stage, approver = `pilot.admin`. Require requestor justification = **Yes**. Under Lifecycle: expiry **Number of days = 180**, and **Allow users to request a specific timeline = Yes**. Enable the policy. |
| 6 | Access package > Overview | Copy the **My Access portal link**. |
| 7 | App > Reference data | As Maintainer, create a new draft of mapping `A-MAP-ENG-DEV` (SAMPLE Data Engineer, Dev, `SAMPLE-GRP-ALPHA-DEV-ENG`) with that link, then **Publish** it. Register yourself as Maintainer first, via `seed_sample_data.py --register-user … --roles PMO,Reviewer,Maintainer` (in a new Cloud Shell session run `pip install --user -r tools/requirements.txt` first; the roles list replaces the old one). Check `/api/me` shows the role. |
| 8 | Cloud Shell | **Stage A** (`enableHandoff: true`, `deployWorker: false`): `whatif` → **approved** → `apply`. Changes policy, budget and container only; ₹0. Azure checks new resources against the policy that is *live*, so the worker cannot go in the same deployment. |
| 9 | Cloud Shell | Wait ~30 min. **Stage B** (`deployWorker: true`): `whatif` → **approved** → `apply`. This deploys the worker, grants Graph read, and turns on `ENTRA_HANDOFF_ENABLED`. Run `tools/deploy_free.sh grant-graph <sub>` if `apply` could not grant it. |

## E2E test script
1. As PMO: create a request with role `SAMPLE Data Engineer`, Dev, joiner `sample.joiner@<tenant>.onmicrosoft.com`, and dates within 180 days. Analyze it; it should show *Ready for human review* and *Matched*.
2. As Reviewer: record *Accepted*. The panel *Access in Microsoft Entra* should show *Waiting for the new joiner to request* and the link.
3. In a private browser window, sign in as `sample.joiner` (change the temporary password), open the link and request access. Enter the reviewed start and end dates and a justification.
4. Within 5 minutes the app should show *Waiting for Entra approval*.
5. As `pilot.admin`, approve at https://myaccess.microsoft.com > Approvals.
6. Within 5 minutes the app should show *Access delivered by Entra* with the end date. Confirm in Entra that `sample.joiner` is a member of `SAMPLE-GRP-ALPHA-DEV-ENG`.
7. Optional: request a later end date than reviewed. The app should flag *Entra's end date is later than the reviewed end date*.
8. Optional: deny a second request. The app should show *Denied in Entra*.

## Phase 2b — automatic assignment (approved by Gokul, 16 Oct 2026)
Choices: **Reviewer Accept = access decision**, **four-eyes on**, **admin script for setup**. Off by default.

**Flow:** PMO creates the request (with joiner UPN) → Analyze → a **second Reviewer** (not the creator or analyser) records *Accepted* → hand-off is **Queued** → within 5 minutes the worker files one Entra `adminAdd` request (allowed catalog, "PMO Review auto-assign" policy, reviewed start/end) → Entra adds the joiner to the group → *Access delivered by Entra* → Entra removes it on the end date (*Expired*). No My Access clicks, no Entra approver.

**Setup (once):**
| # | Where | Step |
|---|---|---|
| 1 | Cloud Shell (Global Admin) | `bash tools/setup_entra.sh package <tenant>.onmicrosoft.com` — device-code sign-in, then creates/reuses the catalog, package and the "PMO Review auto-assign" policy, publishes mapping `A-MAP-ENG-DEV` with the link if needed, and sets `enableAutoAssign` + `entraCatalogId` in `.local/params.json`. |
| 1b | Entra admin center (if device-code sign-in is unavailable) | Identity Governance > Access packages > the package > **Policies > + Add policy**: name exactly `PMO Review auto-assign`; Requests: **None (administrator direct assignments only)**; Lifecycle: expire after **180 days**; Create. Then in Cloud Shell write `.local/entra-package.json` with `catalogId`, `accessPackageId`, `myAccessLink` and run `tools/deploy_free.sh params <sub>`. |
| 2 | Cloud Shell | `tools/deploy_free.sh whatif …` → **approved** → `apply …`. No new resources; changes worker + app settings, then `grant-graph` adds `EntitlementManagement.ReadWrite.All` and `User.ReadBasic.All`. |
| 3 | App | A second person signs in once (e.g. `pilot.admin`), reads the user id from `/api/me`, and is registered: `seed_sample_data.py --register-user <id> --project SAMPLE-ALPHA --roles Reviewer`. |

**E2E test (auto):**
1. As PMO (Gmail): new request, `SAMPLE Data Engineer`, Dev, joiner `sample.joiner@<tenant>`, start today, end within 180 days. Analyze → *Ready for human review*, mapping *Matched*.
2. Still as Gmail, record *Accepted* → the panel shows *Cannot hand off* (same person; expected four-eyes check).
3. In a private window as `pilot.admin` (second Reviewer): open the request, record *Accepted* → *Accepted, Entra request queued*.
4. Within 5 minutes: *Waiting for Entra approval* or *Approved, access being added*, then *Access delivered by Entra* with the reviewed end date.
5. Entra admin center > Groups > `SAMPLE-GRP-ALPHA-DEV-ENG` > Members shows `sample.joiner`; Identity Governance > the access package > Assignments shows it under policy "PMO Review auto-assign".

**Failure categories (panel: "Last status check failed (…)")**
| Category | Meaning / action |
|---|---|
| `GraphForbidden` | Permissions not granted or not yet effective: `grant-graph`, wait ~30 min. |
| `PolicyNotFound` | The package has no "PMO Review auto-assign" policy: re-run `setup_entra.sh package`. |
| `CatalogNotAllowed` | The mapping's package is in a different catalog than `entraCatalogId`: fix the mapping or the parameter. |
| `JoinerNotFound` | No user with that UPN in the tenant: correct the request, Analyze, accept again. |
| `AssignmentRejected` | Entra refused the request (e.g. an open request already exists, or the schedule breaks the policy, F37). Check the access package's Requests tab. |
| `AutoAssignOff` | The app queued it but the worker is not in auto-assign mode: check `ENTRA_AUTO_ASSIGN` on the worker. |

To switch auto-assign off: set `enableAutoAssign` false, what-if, apply (the worker and app go back to link mode; queued records stay queued and show `AutoAssignOff`).

## Validation
- The worker log shows `handoff-sync done checked=… changed=…`.
- No write call to Graph: the worker identity holds only `EntitlementManagement.Read.All` (`grant-graph` prints the list).
- Cost: run `tools/cost_check.sh rg-pmo-review-pilot` daily for the first 3 days, then weekly. The budget alert is at 50% of ₹2,000.

## Failure handling

| Symptom | Action |
|---|---|
| The hand-off says *Cannot hand off* | Read the reason. Usually the mapping has no link, the joiner field is empty, or the request was edited after the review. |
| The status never changes and *Last status check failed (GraphForbidden)* | Run `grant-graph`, then wait about 10 minutes for the token cache to refresh. |
| `AccessPackageNotFound` | The mapping link points to a deleted package. Publish a corrected mapping version and re-analyze. |
| Worker 403 on Cosmos | The data role is scoped to the `handoffs` container (F31, unverified). Widen the scope to `/dbs/pmoreview` in `handoff.bicep` and redeploy. |
| Cost alert | `workflows/incident-recovery.md`. To stop all worker cost: `az functionapp stop`, or delete the worker and its storage. Everything else keeps working. |
