#!/usr/bin/env bash
# Detect THIS machine's public IPv4 (run it on your own device, not in a cloud container)
# and warn if it looks dynamic. Read-only. Records it in state as NOT yet confirmed.
#   bash purview-dbx/tools/my_ip.sh            detect + record (unconfirmed)
#   bash purview-dbx/tools/my_ip.sh --confirm  mark the recorded IP as confirmed by you
set -euo pipefail
TOOL=my_ip
. "$(cd "$(dirname "$0")" && pwd)/lib.sh"
need curl; need python3

if [ "${1:-}" = "--confirm" ]; then
  ip=$(state get inputs.my_ip.value)
  [ -n "$ip" ] && [ "$ip" != "null" ] || die "no IP recorded yet; run without --confirm first"
  state set inputs.my_ip.confirmed_by_user true
  log "confirmed by user: $ip/32"
  exit 0
fi

a=$(curl -4 -sS --max-time 10 https://ifconfig.me || true)
b=$(curl -4 -sS --max-time 10 https://api.ipify.org || true)
log "ifconfig.me=$a ipify=$b"
[ -n "$a" ] || die "could not detect the public IP"

warn=""
[ -n "$b" ] && [ "$a" != "$b" ] && warn="two services saw different IPs ($a / $b): your traffic leaves through more than one address (VPN, proxy or carrier NAT). An allow list with one /32 may lock you out."

# Ownership and reverse DNS hint (RDAP is the public registry lookup).
info=$(python3 - "$a" <<'PY'
import json, socket, sys, urllib.request, ipaddress
ip = sys.argv[1]
out = {"ip": ip}
addr = ipaddress.ip_address(ip)
out["private_or_cgnat"] = addr.is_private or addr in ipaddress.ip_network("100.64.0.0/10")
try:
    out["rdns"] = socket.gethostbyaddr(ip)[0]
except Exception:
    out["rdns"] = None
try:
    with urllib.request.urlopen(f"https://rdap.org/ip/{ip}", timeout=10) as r:
        d = json.load(r)
    out["network"] = d.get("name")
    out["range"] = f"{d.get('startAddress')} - {d.get('endAddress')}"
    names = []
    for e in d.get("entities", []):
        for v in (e.get("vcardArray") or [None, []])[1]:
            if v[0] == "fn":
                names.append(v[3])
    out["owner"] = ", ".join(names[:3])
    rem = " ".join(" ".join(r.get("description", [])) for r in d.get("remarks", []))
    out["remarks"] = None if "BEGIN CERTIFICATE" in rem else rem[:300]
except Exception as e:
    out["rdap_error"] = str(e)[:120]
text = " ".join(str(v) for v in out.values() if v).lower()
hints = [k for k in ("dynamic", "dyn", "dsl", "broadband", "residential", "pool", "ppp", "dhcp", "mobile", "cust", "fiber", "ftth", "cable") if k in text]
out["dynamic_hints"] = hints
print(json.dumps(out))
PY
)
log "rdap: $info"
hints=$(python3 -c 'import json,sys;d=json.loads(sys.argv[1]);print(",".join(d.get("dynamic_hints",[])))' "$info")
cgnat=$(python3 -c 'import json,sys;print(json.loads(sys.argv[1]).get("private_or_cgnat"))' "$info")
[ "$cgnat" = "True" ] && die "$a is private or carrier NAT; it cannot be used in an IP access list"
if [ -n "$hints" ]; then
  warn="${warn:+$warn }Looks like a consumer ISP range ($hints): the address can change after a router restart or lease renewal. If it changes, you lose access until the allow list is updated (rollback in workflows/F-access-lockdown.md)."
fi

state set inputs.my_ip "$(python3 -c 'import json,sys;print(json.dumps({"value":sys.argv[1],"confirmed_by_user":False,"dynamic_warning":sys.argv[2] or None,"source":"my_ip.sh on the user device","lookup":json.loads(sys.argv[3])}))' "$a" "$warn" "$info")"
echo "Public IPv4: $a"
[ -n "$warn" ] && echo "WARNING: $warn"
echo "Not used until you confirm it: bash purview-dbx/tools/my_ip.sh --confirm"
