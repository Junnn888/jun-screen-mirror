#!/usr/bin/env bash
# Build the Rust core and run the C# -> core ping tests against the native lib.
# Works on macOS/Linux/Windows-bash; resolves the platform lib name.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

"$ROOT/scripts/build-core.sh" debug

case "$(uname -s)" in
  Darwin)            LIB="$ROOT/core/target/debug/libscreenbridge_ffi.dylib" ;;
  MINGW*|MSYS*|CYGWIN*) LIB="$ROOT/core/target/debug/screenbridge_ffi.dll" ;;
  *)                 LIB="$ROOT/core/target/debug/libscreenbridge_ffi.so" ;;
esac

export SCREENBRIDGE_CORE_LIB="$LIB"
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}"
dotnet test "$ROOT/apps/windows/ScreenBridge.Interop.Tests/ScreenBridge.Interop.Tests.csproj"
