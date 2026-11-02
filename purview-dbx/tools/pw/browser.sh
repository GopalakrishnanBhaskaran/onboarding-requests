#!/usr/bin/env bash
# One playwright-cli browser session per agent (-s=<agent>), headed, so you can see it.
# Run on YOUR computer: you sign in yourself in the window; this tool never types on a
# sign-in, MFA or password page (it refuses and exits 3).
#
#   browser.sh <agent> open <url>                    start the session (loads saved sign-in if any)
#   browser.sh <agent> save-auth                     after you signed in: save cookies for the other agents
#   browser.sh <agent> goto <url>
#   browser.sh <agent> check-signin                  exit 3 if the page asks for sign-in/MFA
#   browser.sh <agent> shot <workstream> <step> <name>   evidence/<workstream>-<step>-<name>.png
#   browser.sh <agent> snapshot | find <text> | click <ref> | select <ref> <value> | press <key>
#   browser.sh <agent> fill <ref> <text>             refused on sign-in pages
#   browser.sh <agent> close
#
# The saved sign-in (.local/pw-auth.json, mode 600, not in git) is deleted by: browser.sh any forget-auth
set -euo pipefail
TOOL=browser
. "$(cd "$(dirname "$0")/.." && pwd)/lib.sh"
PW="${PLAYWRIGHT_CLI:-npx -y @playwright/cli@0.1.23}"
AUTH_DIR="$PVD_ROOT/.local"; AUTH="$AUTH_DIR/pw-auth.json"
mkdir -p "$AUTH_DIR"; chmod 700 "$AUTH_DIR"

[ $# -ge 2 ] || { sed -n 2,16p "$0"; exit 2; }
AGENT="$1"; CMD="$2"; shift 2
TOOL="browser-$AGENT"
pw() { $PW -s="$AGENT" "$@"; }

current_url() { pw --raw eval "location.href" 2>/dev/null | tr -d '"' || true; }

is_signin() {
  case "$(current_url)" in
    *login.microsoftonline.com*|*login.microsoft.com*|*login.live.com*|*account.live.com*|*device.login*|*microsoft.com/devicelogin*|*aka.ms/mfasetup*|*mysignins.microsoft.com*|*/oauth2/*authorize*|*accounts.azuredatabricks.net/login*|*/login.html*) return 0 ;;
    *) return 1 ;;
  esac
}

signin_stop() {
  log "SIGN-IN NEEDED in session '$AGENT': please sign in yourself in the browser window, then tell the orchestrator. Nothing was typed."
  state step "$AGENT" browser blocked "sign-in needed" >/dev/null 2>&1 || true
  exit 3
}

case "$CMD" in
  open)
    url="${1:-about:blank}"
    pw open "$url" --headed --persistent
    if [ -f "$AUTH" ]; then pw state-load "$AUTH" >/dev/null && pw goto "$url" >/dev/null; fi
    log "open $url"
    is_signin && signin_stop || true ;;
  save-auth)
    is_signin && signin_stop
    pw state-save "$AUTH" >/dev/null; chmod 600 "$AUTH"
    log "sign-in state saved for other sessions (local only, delete with forget-auth)" ;;
  forget-auth)
    rm -f "$AUTH"; log "saved sign-in deleted" ;;
  goto)
    pw goto "$1" >/dev/null; log "goto $1"
    is_signin && signin_stop || true ;;
  check-signin)
    is_signin && signin_stop; echo "signed in: $(current_url)" ;;
  shot)
    [ $# -eq 3 ] || die "shot <workstream> <step> <name>"
    f="$PVD_EVIDENCE/$1-$2-$3.png"
    pw screenshot --filename="$f" >/dev/null 2>&1 || pw run-code "async page => { await page.screenshot({ path: '$f', fullPage: true }); }" >/dev/null
    log "evidence $f"; echo "$f" ;;
  fill|type)
    is_signin && { log "REFUSED: $CMD on a sign-in page"; signin_stop; }
    pw "$CMD" "$@" ;;
  close)
    pw close || true ;;
  *)
    pw "$CMD" "$@" ;;
esac
