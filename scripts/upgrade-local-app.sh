#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-dotnet}"
export DOTNET
export PATH="$(dirname "$(command -v "$DOTNET")"):$PATH"
OUT="${1:?An evidence directory is required}"

for tool in uv python3 mutter Xwayland xclip; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    printf 'ERROR: Upgrade verification requires %s. See scripts/verification/README.md. No installation was changed.\n' "$tool" >&2
    exit 1
  fi
done

uv run --no-project scripts/restart_local_app.py --check-idle
# Prepare and validate a matching app and bundle before replacing the installation.
bash scripts/install-linux-app.sh --publish-only
uv run --no-project scripts/verification/verify_upgrade.py --out "$OUT"
uv run --no-project scripts/restart_local_app.py --check-idle
bash scripts/install-linux-app.sh --from-publish
uv run --no-project scripts/restart_local_app.py --installed
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
"$DOTNET" run --project scripts/verification/plugin_compatibility -- \
  "$DATA_HOME/typewhisper-app" "$DATA_HOME/TypeWhisper/Plugins" '*'
uv run --no-project scripts/verification/doctor.py
