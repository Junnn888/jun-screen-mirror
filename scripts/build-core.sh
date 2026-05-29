#!/usr/bin/env bash
# Build the ScreenBridge Rust core and (re)generate include/screenbridge.h.
# Usage: scripts/build-core.sh [debug|release]   (default: debug)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE="${1:-debug}"

ARGS=()
[ "$PROFILE" = "release" ] && ARGS+=(--release)

# Match the macOS app's deployment floor (PLAN.md §3) so the native lib links
# into the SwiftUI app without a version-mismatch warning.
if [ "$(uname)" = "Darwin" ]; then
  export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-13.0}"
fi

cargo build --manifest-path "$ROOT/core/Cargo.toml" "${ARGS[@]}"
echo "Generated header: $ROOT/include/screenbridge.h"
