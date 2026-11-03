#!/usr/bin/env bash
# Joins the open storage-admin requests of all agents into one short e-mail you can send (read-only).
# Usage: bash admin_request.sh [--to-name <team name>]
source "$(dirname "$0")/lib.sh"
TO="team"; [ "${1:-}" = --to-name ] && TO="$2"
STG="$(env_get storageAccount)"; WS="$(env_get fabricWorkspace | cut -d' ' -f1)"
FILES="$(ls "$RAW_DIR"/ADMIN-REQUEST-*.txt 2>/dev/null || true)"   # per-agent files (re-run an agent's plan to refresh its file)
[ -n "$FILES" ] || { echo "No open admin requests."; exit 0; }
BODY="$RAW_DIR/ADMIN-EMAIL.txt"
{
echo "Subject: Fabric POC - please apply small changes on $STG (dev)"
echo
echo "Hi $TO,"
echo
echo "For a Fabric POC on the dev storage account $STG I need the changes below. My account can't make them."
echo "Nothing else changes: public access stays Disabled, no firewall or DNS change; POC data stays in the new empty container fabric-poc."
echo
cat $FILES | python3 -c '
import sys
blocks=[]; 
for line in sys.stdin.read().splitlines():
    if line.startswith("- "): blocks.append([line[2:]])
    elif blocks: blocks[-1].append(line.strip())
blocks.sort(key=lambda b: "[optional" in b[0])   # required first, optional last
for i,b in enumerate(blocks,1):
    print(f"{i}. {b[0]}"); [print("   "+x) for x in b[1:]]
' 
echo
echo "Each item has its rollback command. The POC ends with the rollbacks and deleting fabric-poc."
echo
echo "Thanks,"
echo "Gokul"
} > "$BODY"
cat "$BODY"
save "ADMIN-EMAIL.txt" < "$BODY" >/dev/null
