# F Access lockdown to your device (wave 2, last)

**Objective:** the workspace UI/API answers only your IP and the private path; every other public IP gets 403.

**Inputs:** `inputs.my_ip` with `confirmed_by_user=true` (stop otherwise), wave 2 approval, `access.before.*`.

**Locks:** `workspace`, `purview`.

**Never lock you out — order matters**
1. Before: `GET /api/2.0/workspace-conf?keys=enableIpAccessLists`, `GET /api/2.0/ip-access-lists` → `access.before`.
2. Run every step **from your device** (its IP is the one allowed). Not from the cloud session.
3. Create the allow list first (it has no effect while the feature is off):
   `POST /api/2.0/ip-access-lists {"label":"my-device","list_type":"ALLOW","ip_addresses":["<my-ip>/32"]}`
4. Check the list contains exactly your confirmed IP.
5. Enable: `PATCH /api/2.0/workspace-conf {"enableIpAccessLists":"true"}`.
6. Immediately, from your device: UI opens and `GET /api/2.0/clusters/list` → 200. If not → rollback A at once.
7. From the cloud runner (another public IP): expect **403** (IP blocked). Evidence `F-07-other-ip-blocked.png`.
8. From the SHIR VM (private path): `curl` the workspace → 401/200, not 403. Records whether PE traffic is subject to the IP list (DOC_CHECK D7, UNVERIFIED until this step).
9. Purview firewall (after scans pass in wave 3): *Disabled for ingestion only*. No IP allow list exists for Purview (D5).
10. Not done (docs): account-level IP list, workspace storage firewall (D6).

**Rollback**
- A. From your device: `PATCH /api/2.0/workspace-conf {"enableIpAccessLists":"false"}` (lists stay, nothing filtered).
- B. If your IP changed and you are locked out: run the same call **from the SHIR VM** through `az vm run-command` (private path, step 8 must have shown it is not filtered) with a token from your `az account get-access-token --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`, or add the new IP the same way.
- C. Update the list: `PATCH /api/2.0/ip-access-lists/{id} {"ip_addresses":["<new-ip>/32"]}`.
- D. Purview firewall back to *Enabled from all networks* in the Azure portal.

**Expected output:** `access.after.*` (feature on, list id, IPs), before/after table for the report.
