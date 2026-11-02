#!/usr/bin/env bash
# Shared helpers for the Fabric -> ADLS Gen2 (private) POC.
# Read-only by default. Nothing here changes Azure or Fabric.
set -Eeuo pipefail
trap 'printf "%s ERROR line %s: %s\n" "$(date -u +%H:%M:%SZ)" "$LINENO" "$BASH_COMMAND" >&2' ERR

POC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_DIR="$(cd "$POC_DIR/.." && pwd)"
RAW_DIR="${RAW_DIR:-$REPO_DIR/.local/fabric-poc}"   # raw output, never in git (.local/ is ignored)
EVIDENCE_DIR="$POC_DIR/evidence"                     # masked output, safe for git
FABRIC_API="https://api.fabric.microsoft.com/v1"
FABRIC_RESOURCE="https://api.fabric.microsoft.com"
mkdir -p "$RAW_DIR" "$EVIDENCE_DIR"
# Working state (approvals, locks, ids) lives outside git so 'git pull' never conflicts; state/state.json is the template + handoff copy.
export STATE_FILE="${STATE_FILE:-$RAW_DIR/state.json}"
[ -f "$STATE_FILE" ] || cp "$POC_DIR/state/state.json" "$STATE_FILE"

log()  { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; }
die()  { log "ERROR: $*"; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 not found"; }

# Mask GUIDs (keep first 4 chars), public IPv4 (keep first two octets), e-mail local parts.
# Keeps resource names readable; removes subscription/tenant/object ids and the device IP.
# Private (RFC1918) addresses such as private endpoint IPs are kept: they are not personal.
mask() {
  python3 -c '
import re,sys,ipaddress
t=sys.stdin.read()
t=re.sub(r"\b([0-9a-fA-F]{4})[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", r"\1****-****", t)
def ip(m):
    try:
        a=ipaddress.ip_address(m.group(0))
    except ValueError:
        return m.group(0)
    return m.group(0) if (a.is_private or a.is_loopback) else ".".join(m.group(0).split(".")[:2]+["x","x"])
t=re.sub(r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", ip, t)
t=re.sub(r"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})", r"***@\1", t)
t=re.sub(r"(sig=|AccountKey=|SharedAccessSignature=|client_secret=|\"secret\": ?\"|\"accessToken\": ?\")[^\"&; ]+", r"\1******", t)
sys.stdout.write(t)'
}

# save <name> : stdin -> raw file + masked evidence file
save() {
  local name="$1" raw="$RAW_DIR/$1"
  cat > "$raw"
  mask < "$raw" > "$EVIDENCE_DIR/$name"
  log "saved evidence/$name"
}

# Fabric REST (GET by default). Token comes from the signed-in az CLI user; never printed.
fab() {
  local url="$1" method="GET"; shift
  if [ $# -gt 0 ] && [[ "$1" != -* ]]; then method="$1"; shift; fi
  az rest --method "$method" --resource "$FABRIC_RESOURCE" --url "$FABRIC_API$url" "$@"
}

# Device public IP as seen from the internet (only meaningful when run on your own device).
device_ip() { curl -fsS -m 10 https://api.ipify.org || curl -fsS -m 10 https://ifconfig.me; }

# Fail early, with a fix, when the Azure CLI itself is broken (e.g. a stale command index in Cloud Shell).
az_preflight() {
  az account show --query id -o tsv >/dev/null 2>"$RAW_DIR/.azerr" && return 0
  die "az is not working: $(grep -m1 -i error "$RAW_DIR/.azerr" || head -1 "$RAW_DIR/.azerr"). Fix: rm -f ~/.azure/commandIndex.json ; az account show -o table ; then re-run"
}

# Approval gate for any change: wave must be approved in state/state.json.
require_approval() {
  local wave="$1"
  python3 "$POC_DIR/tools/state.py" check-approval "$wave" || die "wave $wave is not approved (run: tools/state.py approve $wave --by '<name> <date>')"
}
