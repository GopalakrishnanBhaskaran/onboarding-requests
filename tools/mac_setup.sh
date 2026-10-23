#!/usr/bin/env bash
# One-time set-up to deploy and run the pilot tools from a Mac instead of Azure Cloud Shell (whose home folder is reset
# after a short idle time). Installs ready-built tools (no compiling), signs in to Azure, builds .local/params.json.
# Changes nothing in Azure. Safe to run again.
#
#   bash tools/mac_setup.sh <subscriptionId>
#
# Then, in Terminal, type `bash` first (zsh does not split "$COMMON"-style variables) and use the same commands as in
# Cloud Shell, e.g. tools/deploy_free.sh app <subscriptionId>. See docs/MAC_DEPLOY.md.
set -euo pipefail
SUB="${1:?usage: bash tools/mac_setup.sh <subscriptionId>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

[[ "$(uname -s)" == "Darwin" ]] || { echo "This script is for macOS. On Linux or Cloud Shell use the tools directly."; exit 1; }

# Nothing is compiled: on Intel Macs Homebrew has no new prebuilt packages since Sept 2026 (Tier 3, F69), so a formula
# such as python@3.15 builds from source for a long time with no output. Ready-built pieces only:
#   az      Homebrew cask (Microsoft's build, its own Python; Learn: macOS 13 or later)
#   node    official nodejs.org build, checksum-verified, in ~/.pmo-tools/node (Vite needs ^20.19 or >=22.12)
#   python  the Mac's own python3 (3.9 or later is enough for the tools)
#   .NET    dotnet-install.sh into ~/.dotnet
TOOLS="$HOME/.pmo-tools"
macos=$(sw_vers -productVersion 2>/dev/null || echo 0)
echo "macOS $macos, $(uname -m)"

echo "== Azure CLI (az) =="
BREW=""
for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do if [[ -x "$b" ]]; then BREW="$b"; break; fi; done
if [[ -n "$BREW" ]] && ! command -v az >/dev/null 2>&1; then eval "$("$BREW" shellenv)"; fi
if command -v az >/dev/null 2>&1; then
  echo "  already installed: $(command -v az)"
else
  if [[ -z "$BREW" ]]; then
    echo "Homebrew is missing. Install it (https://brew.sh), then run this script again:"
    echo '  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"'
    exit 1
  fi
  (( ${macos%%.*} >= 13 )) || echo "  WARNING: the Azure CLI package needs macOS 13 or later (this Mac: $macos). If it fails, use Azure Cloud Shell."
  # New Terminal windows need Homebrew on the PATH too (the step the Homebrew installer asks for).
  if ! grep -qs "brew shellenv" "$HOME/.zprofile"; then
    printf '\neval "$(%s shellenv)"\n' "$BREW" >> "$HOME/.zprofile"
    echo "  Added Homebrew to ~/.zprofile (new Terminal windows find az)."
  fi
  HOMEBREW_NO_INSTALL_UPGRADE=1 "$BREW" install --cask azure-cli
fi
echo "  az $(az version --query '"azure-cli"' -o tsv)"

echo "== Node.js (web build and upload) =="
node_ok() { "$1" -e 'const [a,b]=process.versions.node.split(".").map(Number);process.exit(a>22||(a===22&&b>=12)||(a===20&&b>=19)?0:1)' 2>/dev/null; }
if command -v node >/dev/null && node_ok node; then
  echo "  node $(node --version) ($(command -v node))"
elif [[ -x "$TOOLS/node/bin/node" ]] && node_ok "$TOOLS/node/bin/node"; then
  echo "  node $("$TOOLS/node/bin/node" --version) ($TOOLS/node)"
else
  arch=$([[ "$(uname -m)" == "arm64" ]] && echo arm64 || echo x64)
  base=https://nodejs.org/dist/latest-v22.x
  line=$(curl -fsSL "$base/SHASUMS256.txt" | grep -E "  node-v22\.[0-9.]+-darwin-$arch\.tar\.gz$")
  file=${line##* }; sum=${line%% *}
  mkdir -p "$TOOLS"; rm -rf "$TOOLS/node" "$TOOLS/$file"
  echo "  downloading $file from nodejs.org"
  curl -fsSL -o "$TOOLS/$file" "$base/$file"
  [[ "$(shasum -a 256 "$TOOLS/$file" | cut -d' ' -f1)" == "$sum" ]] || { echo "Checksum mismatch for $file: not installed."; rm -f "$TOOLS/$file"; exit 1; }
  tar -xzf "$TOOLS/$file" -C "$TOOLS" && mv "$TOOLS/${file%.tar.gz}" "$TOOLS/node" && rm -f "$TOOLS/$file"
  echo "  node $("$TOOLS/node/bin/node" --version) in $TOOLS/node (the tools add it to their PATH)"
fi

echo "== Python =="
if ! python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>/dev/null; then
  echo "python3 3.9 or later is missing. Install the Xcode command line tools (xcode-select --install) or Python from python.org, then re-run."
  exit 1
fi
echo "  $(python3 --version) ($(command -v python3))"

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
