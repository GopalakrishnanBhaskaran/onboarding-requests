#!/usr/bin/env bash
# Local end-to-end: in-memory SAMPLE API (dev host) behind the SWA CLI emulator with mock sign-in.
#   tools/run_local.sh            start (Ctrl+C stops both)
# Opens http://localhost:4280 ; the emulator's login page lets you pick a mock user id and user details
# (use the SAMPLE ids from tools/sample_data/sample-data.json, e.g. sample-user-alice, with user details
# "<id>@sample.invalid": the named Owner on a request is matched on that sign-in name, e.g. sample-user-olivia@sample.invalid).
# There is no worker or Entra locally: ALLOW_UNVERIFIED_ENTRA=true marks the Entra checks "not checked (local development)".
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "${SKIP_BUILD:-}" != "1" ]]; then
  dotnet build "$ROOT/tools/local-api" -v q
  npm --prefix "$ROOT/src/web" run build
fi

# AGENT_TENANT_ID: a SAMPLE tenant id, so the SAMPLE projects (no tenant id of their own) accept mapping uploads (F4 TenantId).
PILOT_LOCAL_DEV=true ALLOW_UNVERIFIED_ENTRA=true AGENT_TENANT_ID="${AGENT_TENANT_ID:-00000000-0000-4000-8000-0000000000a1}" \
  dotnet run --project "$ROOT/tools/local-api" --no-build &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -s -o /dev/null http://127.0.0.1:7071/api/me && break; sleep 1; done

cd "$ROOT/src/web"
npx swa start dist --api-devserver-url http://127.0.0.1:7071 --swa-config-location "$ROOT" --port 4280
