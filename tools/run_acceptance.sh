#!/usr/bin/env bash
# Run the pilot acceptance suite and write docs/PILOT_REPORT.md.
#   tools/run_acceptance.sh          acceptance cases (in-process, SAMPLE data)
#   E2E=1 tools/run_acceptance.sh    also run Playwright against tools/run_local.sh (must already be running)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/tests/acceptance/out"
rm -rf "$OUT" && mkdir -p "$OUT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build "$ROOT/tests/PmoReview.Tests" -v q -nologo
TESTS_DLL="$ROOT/tests/PmoReview.Tests/bin/Debug/net10.0/PmoReview.Tests.dll"

set +e
ACCEPTANCE_OUT="$OUT/evidence.jsonl" dotnet "$TESTS_DLL" -trait "Category=Acceptance" -result-ctrf "$OUT/acceptance.ctrf.json" -noLogo
ACC_RC=$?
dotnet "$TESTS_DLL" -trait- "Category=Acceptance" -result-ctrf "$OUT/unit.ctrf.json" -noLogo -reporter quiet
UNIT_RC=$?
E2E_ARG=()
if [[ "${E2E:-}" == "1" ]]; then
  (cd "$ROOT/src/web" && npx playwright test --reporter=json > "$OUT/e2e.json")
  E2E_ARG=(--e2e "$OUT/e2e.json")
fi
set -e

python3 -I "$ROOT/tools/export_pilot_report.py" \
  --cases "$ROOT/tests/acceptance/cases.json" \
  --evidence "$OUT/evidence.jsonl" \
  --results "$OUT/acceptance.ctrf.json" \
  --unit "$OUT/unit.ctrf.json" \
  --commit "$(git -C "$ROOT" rev-parse --short HEAD)" \
  ${E2E_ARG[@]+"${E2E_ARG[@]}"} \
  --out "$ROOT/docs/PILOT_REPORT.md"

echo "acceptance exit=$ACC_RC unit exit=$UNIT_RC"
exit $(( ACC_RC != 0 || UNIT_RC != 0 ))
