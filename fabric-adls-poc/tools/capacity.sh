#!/usr/bin/env bash
# Pause / resume / show the POC Fabric capacity (F2 bills per second while Active, FB18/FB19).
# Usage: bash capacity.sh status|pause|resume [--name fabpocf2]
# ARM: POST .../Microsoft.Fabric/capacities/{name}/suspend|resume?api-version=2023-11-01
#   https://learn.microsoft.com/en-us/rest/api/microsoftfabric/fabric-capacities/suspend (read 2026-11-03: POST, 200/202)
# Pausing is reversible and changes no network, firewall or role. While paused, Fabric items on the capacity can't run.
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"][sys.argv[2]])' "$STATE_FILE" "$1"; }
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"
case "$SUB" in *'*'*|*UNVERIFIED*|"") SUB="$(az account show --query id -o tsv)";; esac
ACTION="${1:-status}"; shift || true
NAME=fabpocf2
while [ $# -gt 0 ]; do case "$1" in --name) NAME="$2"; shift 2;; *) die "unknown argument $1";; esac; done
URL="https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Fabric/capacities/$NAME"
state() { az rest --method get --url "$URL?api-version=2023-11-01" --query "properties.state" -o tsv; }
case "$ACTION" in
  status) echo "$NAME: $(state)";;
  pause|resume)
    verb=$([ "$ACTION" = pause ] && echo suspend || echo resume)
    az rest --method post --url "$URL/$verb?api-version=2023-11-01" -o none
    want=$([ "$ACTION" = pause ] && echo 'Paused|Suspended' || echo Active)   # state enum names: UNVERIFIED, so both are accepted
    for i in $(seq 1 30); do s="$(state)"; echo "$s" | grep -qxE "$want" && break; sleep 10; done
    echo "$NAME: $s"; echo "$s" | grep -qxE "$want" || die "expected $want, got $s"
    echo "$(date -u +%FT%TZ) $NAME $ACTION -> $s" | save "CAP-$ACTION-$(date -u +%H%M).txt";;
  *) die "usage: capacity.sh status|pause|resume [--name <capacity>]";;
esac
