#!/usr/bin/env bash
# One-time set-up to deploy and run the pilot tools from a Mac instead of Azure Cloud Shell (whose home folder is reset
# after a short idle time). Installs the tools with Homebrew, signs in to Azure, builds .local/params.json. Changes
# nothing in Azure.
#
#   bash tools/mac_setup.sh <subscriptionId>
#
# Then, in Terminal, type `bash` first (zsh does not split "$COMMON"-style variables) and use the same commands as in
# Cloud Shell, e.g. tools/deploy_free.sh app <subscriptionId>. See docs/MAC_DEPLOY.md.
set -euo pipefail
SUB="${1:?usage: bash tools/mac_setup.sh <subscriptionId>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

[[ "$(uname -s)" == "Darwin" ]] || { echo "This script is for macOS. On Linux or Cloud Shell use the tools directly."; exit 1; }

# Homebrew: installed by you (it asks for your password), not by this script.
BREW=""
for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do [[ -x "$b" ]] && { BREW="$b"; break; }; done
if [[ -z "$BREW" ]]; then
  echo "Homebrew is missing. Install it (https://brew.sh), then run this script again:"
  echo '  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"'
  exit 1
fi
eval "$("$BREW" shellenv)"
# New Terminal windows need Homebrew on the PATH too (the step the Homebrew installer asks for).
if ! grep -qs "brew shellenv" "$HOME/.zprofile"; then
  printf '\neval "$(%s shellenv)"\n' "$BREW" >> "$HOME/.zprofile"
  echo "Added Homebrew to ~/.zprofile (new Terminal windows find az, node, python)."
fi

echo "== Tools (Homebrew) =="
# azure-cli: az; bash: a current bash next to macOS's 3.2; node: web build + SWA CLI (Vite needs >= 20.19); python: tools.
"$BREW" install azure-cli bash node python # already installed: Homebrew says so and goes on
command -v zip >/dev/null || "$BREW" install zip
echo "  az $(az version --query '"azure-cli"' -o tsv), node $(node --version), $(python3 --version), bash $("$(brew --prefix)/bin/bash" -c 'echo $BASH_VERSION')"

# The SWA CLI uploads with Microsoft's StaticSitesClient, built for Intel Macs (osx-x64): Apple silicon runs it with Rosetta 2.
if [[ "$(uname -m)" == "arm64" ]] && ! /usr/bin/arch -x86_64 /usr/bin/true 2>/dev/null; then
  echo "== Rosetta 2 (needed by the Static Web Apps upload client on Apple silicon; asks for your password) =="
  sudo /usr/sbin/softwareupdate --install-rosetta --agree-to-license
fi

echo "== .NET 10 SDK (user folder ~/.dotnet, no admin; the same as deploy_free.sh does) =="
if ! { command -v dotnet >/dev/null && dotnet --list-sdks | grep -q '^10\.'; } && \
   ! { [[ -x "$HOME/.dotnet/dotnet" ]] && "$HOME/.dotnet/dotnet" --list-sdks | grep -q '^10\.'; }; then
  mkdir -p "$ROOT/.local"
  curl -sSL -o "$ROOT/.local/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
  bash "$ROOT/.local/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
fi
if [[ -x "$HOME/.dotnet/dotnet" ]]; then echo "  ~/.dotnet: $("$HOME/.dotnet/dotnet" --list-sdks | tr '\n' ' ')"; else echo "  $(dotnet --list-sdks | tr '\n' ' ')"; fi

echo "== Python packages for the tools (private venv under .local, git-ignored) =="
echo "  interpreter: $("$ROOT/tools/cosmos_python.sh")"

echo "== Azure sign-in =="
if ! az account show >/dev/null 2>&1; then az login --output none; fi
if ! az account set --subscription "$SUB" 2>/dev/null; then
  echo "Subscription $SUB is not visible to this sign-in. Sign in to its tenant: az login --tenant <tenant id>, then re-run."
  exit 1
fi
echo "  $(az account show --query "{subscription:name, tenant:tenantId, user:user.name}" -o tsv)"
az bicep install --only-show-errors >/dev/null 2>&1 || true # what-if / apply only

echo "== Live settings into .local/params.json (read-only) =="
"$ROOT/tools/deploy_free.sh" params "$SUB"

cat <<EOF

Ready. In Terminal type  bash  first, then for example:
  cd "$ROOT" && SUB=$SUB
  git pull -q origin claude/great-goodall-j4ozev && git log --oneline -1
  tools/deploy_free.sh worker \$SUB
  tools/deploy_free.sh app \$SUB
  bash tools/verify_features.sh \$SUB | tee .local/verify.txt
Workbooks: put them in $ROOT/.local/ (git-ignored). Steps: docs/MAC_DEPLOY.md
EOF
