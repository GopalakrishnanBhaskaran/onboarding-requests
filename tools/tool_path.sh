# Sourced by the tools that call az. A Mac Terminal window opened before Homebrew was on its PATH cannot find az:
# load Homebrew's PATH when it is installed; if az is still missing, say how to install it and stop.
# Node from tools/mac_setup.sh lives in ~/.pmo-tools/node.
if [[ -x "$HOME/.pmo-tools/node/bin/node" ]]; then PATH="$HOME/.pmo-tools/node/bin:$PATH"; fi
if ! command -v az >/dev/null 2>&1; then
  for _brew in /opt/homebrew/bin/brew /usr/local/bin/brew /home/linuxbrew/.linuxbrew/bin/brew; do
    if [[ -x "$_brew" ]]; then eval "$("$_brew" shellenv)"; break; fi
  done
  unset _brew
fi
if ! command -v az >/dev/null 2>&1; then
  echo "az (Azure CLI) not found on this machine." >&2
  echo "  Mac: bash tools/mac_setup.sh <subscriptionId>   (installs az, Node and .NET ready-built; docs/MAC_DEPLOY.md)" >&2
  echo "  Other: https://learn.microsoft.com/cli/azure/install-azure-cli, or run in Azure Cloud Shell." >&2
  exit 127
fi
