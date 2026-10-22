#!/usr/bin/env bash
# Prints a Python interpreter that has the Cosmos SDK (tools/requirements.txt), installing it when missing.
# Cloud Shell can drop user-installed packages after a reset; the seed and the diagnosis need this one package.
# Install output goes to stderr; stdout is only the interpreter path.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REQ="$ROOT/tools/requirements.txt"
has_sdk() { "$1" -c "import azure.cosmos, openpyxl" 2>/dev/null; } # Cosmos + the workbook import

has_sdk python3 && { echo python3; exit 0; }
if python3 -m pip install --user -q -r "$REQ" >&2 && has_sdk python3; then echo python3; exit 0; fi
# Fallback when the system Python refuses user installs: a private venv under .local (git-ignored).
VENV="$ROOT/.local/venv"
[[ -x "$VENV/bin/python" ]] || python3 -m venv "$VENV" >&2
"$VENV/bin/python" -m pip install -q -r "$REQ" >&2
has_sdk "$VENV/bin/python" || { echo "Could not install the Cosmos SDK (tools/requirements.txt)." >&2; exit 1; }
echo "$VENV/bin/python"
