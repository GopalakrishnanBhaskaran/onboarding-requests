# V Validation (wave 3)

**Objective:** prove the private path and the lockdown with output, not assumptions.

**Inputs:** `network.*`, `access.after.*`, `inputs.my_ip`.

**Steps** (SHIR VM through `az vm run-command invoke --command-id RunShellScript`; output saved under `validation.*`)
1. `nslookup adb-7405607339118600.0.azuredatabricks.net` → private IP in 10.120.1.0/26.
2. `nslookup <purview>.purview.azure.com` → account PE IP; ingestion storage names → their PE IPs.
3. `nc -zv <host> 443` for the workspace and Purview (Linux equivalent of Test-NetConnection).
4. `curl -s -o /dev/null -w '%{http_code}' https://adb-7405607339118600.0.azuredatabricks.net/api/2.0/clusters/list` → 401 (reached, no credential) — not 403.
5. Same lookups from a pod (`kubectl run --rm -i busybox -- nslookup …`): the SHIR pods resolve private too.
6. Your device: browser opens the workspace (screenshot), API 200.
7. Cloud runner (other public IP): UI shows the IP block page, API 403. Screenshot.

**Expected output:** `validation.{dns,tcp,http,device,other_ip}` with raw output; evidence `V-0N-*.png`.

**Rollback:** none (read-only).
