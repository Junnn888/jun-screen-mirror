#!/usr/bin/env bash
# Build + test the macOS app shell (and the Rust core it links).
# Honours SCREENBRIDGE_CORE_PROFILE (debug|release; default debug).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE="${SCREENBRIDGE_CORE_PROFILE:-debug}"

"$ROOT/scripts/build-core.sh" "$PROFILE"

export SCREENBRIDGE_CORE_PROFILE="$PROFILE"
swift build --package-path "$ROOT/apps/macos"
swift test  --package-path "$ROOT/apps/macos"
