#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

TARGET_DIR="${1:-}"

if [ -z "$TARGET_DIR" ]; then
  echo "Usage: ./scripts/install-hooks.sh <project-directory>"
  echo ""
  echo "Installs Copilot and Claude Code hook configs into the target project."
  exit 1
fi

TARGET_DIR="$(cd "$TARGET_DIR" && pwd)"

echo "Installing beacon hooks into: $TARGET_DIR"
echo ""

mkdir -p "$TARGET_DIR/.github/hooks"
cp "$REPO_ROOT/hook-configs/copilot/CopilotHookSettings.json" "$TARGET_DIR/.github/hooks/CopilotHookSettings.json"
echo "[copilot]  Installed .github/hooks/CopilotHookSettings.json"

mkdir -p "$TARGET_DIR/.claude"
if [ -f "$TARGET_DIR/.claude/settings.json" ]; then
  echo "[claude]   .claude/settings.json already exists — skipping."
  echo "           Manually merge the hooks from: hook-configs/claude-code/settings.json"
else
  cp "$REPO_ROOT/hook-configs/claude-code/settings.json" "$TARGET_DIR/.claude/settings.json"
  echo "[claude]   Installed .claude/settings.json"
fi

echo ""
echo "Done. Make sure the beacon server is running on http://127.0.0.1:17321"
