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
| 7 | App > Reference data | As Maintainer, create a new draft of mapping `A-MAP-ENG-DEV` (SAMPLE Data Engineer, Dev, `SAMPLE-GRP-ALPHA-DEV-ENG`) with that link, then **Publish** it. Register yourself as Maintainer first, via `seed_sample_data.py --register-user … --roles PMO,Reviewer,Maintainer`. |
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
