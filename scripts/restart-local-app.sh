#!/usr/bin/env bash
# Keep the existing entry point for local restart callers.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
exec uv run --no-project "$ROOT/scripts/restart_local_app.py" "$@"
