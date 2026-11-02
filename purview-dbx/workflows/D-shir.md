# D Kubernetes SHIR (VM in wave 1, install in wave 2)

**Objective:** a Purview Kubernetes self-hosted IR (`pvdbx-k8s-shir`) with status **Running**, inside the transit VNet. Windows SHIR is not supported for this source (DOC_CHECK D1).

**Inputs:** `network.transit.shir_subnet_id`, wave 1 approval (VM), wave 2 approval (install).

**Locks:** `vm:pvdbx-shir-vm`.

**Steps**
1. (wave 1) `pvdbx-shir-vm`: Ubuntu 24.04 LTS, Standard_D8as_v5 (8 vCPU, 32 GB; docs minimum 8 cores, 32 GB, 80 GB disk), 128 GB Standard SSD, **no public IP**, SSH key only (private key stays local, never used: all access through `az vm run-command`), boot diagnostics managed, auto-shutdown 20:00 local.
2. (wave 2) run-command: install k3s single node (`--disable traefik`), check `kubectl get nodes` Ready and allocatable pods ≥ 20.
3. Purview portal → Data Map → Source management → Integration runtimes → + New → Self-hosted → name `pvdbx-k8s-shir`, **Kubernetes service support** on → Create → **Get registration key** (you copy it; it is a secret) → download IRCTL (Linux).
4. run-command with the key as a **protected parameter** (never logged, never in state): `irctl create --registration-key <key>` with `KUBECONFIG=/etc/rancher/k3s/k3s.yaml`; then `irctl describe`.
5. Portal shows **Running**; screenshot `evidence/D-05-shir-running.png`. Record IRCTL output (masked).
6. If IRCTL rejects k3s: same VM with kubeadm, or a 1-node AKS (asks again: cost changes).
7. After scans: `az vm deallocate` (cost stops except the disk).

**Expected output:** `network.shir.{vm_id,private_ip}`, `scans.ir.{name,status,version}`.

**Rollback:** `irctl delete` (or delete the IR in Purview), `az vm delete` + disk + NIC.
