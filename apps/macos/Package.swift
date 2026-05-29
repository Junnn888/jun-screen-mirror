// swift-tools-version: 6.0
// Swift 6 language mode (the default at tools-version 6.0) gives compile-time
// data-race & actor-isolation checking for the Phase 1+ capture→encode→render→FFI
// pipeline. Needs a Swift 6 toolchain (Xcode 16+); CI runs on macos-15. Do NOT
// lower this to silence an older-toolchain error — bump the toolchain instead
// (macos-14's default Swift 5.10 cannot even parse a 6.0 manifest). See docs/build.md.
import PackageDescription
import Foundation

// Resolve absolute paths from the manifest's own location so `swift build`
// works from any working directory and across machines/CI. The cbindgen header
// lives at <repo>/include and the cargo-built native lib at
// <repo>/core/target/<profile> — both OUTSIDE this package (PLAN.md §5).
//   apps/macos  ->  <repo>
let packageDir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
let repoRoot = packageDir.deletingLastPathComponent().deletingLastPathComponent()
let includeDir = repoRoot.appendingPathComponent("include").path

// Match the cargo profile that produced libscreenbridge_ffi.dylib. CI exports
// SCREENBRIDGE_CORE_PROFILE=release; local debug builds default to "debug".
let coreProfile = ProcessInfo.processInfo.environment["SCREENBRIDGE_CORE_PROFILE"] ?? "debug"
let coreLibDir = repoRoot.appendingPathComponent("core/target/\(coreProfile)").path

// Pass the include dir to Clang when the importing target compiles the
// CScreenBridge module (the modulemap's `header "shim.h"` is local; the
// `#include "screenbridge.h"` inside it is resolved via this -I).
let cInterop: [SwiftSetting] = [.unsafeFlags(["-Xcc", "-I\(includeDir)"])]

// Link the cdylib and bake an rpath so the loader finds it at run/test time.
// NOTE: a bare "-rpath" is rejected by the Swift driver; it must be passed
// through to ld via -Xlinker (verified on Swift 6.3).
let linkCore: [LinkerSetting] = [.unsafeFlags([
    "-L\(coreLibDir)",
    "-lscreenbridge_ffi",
    "-Xlinker", "-rpath", "-Xlinker", coreLibDir,
])]

let package = Package(
    name: "ScreenBridge",
    // PLAN.md §3: macOS 13+ (ScreenCaptureKit system audio).
    platforms: [.macOS(.v13)],
    targets: [
        // Exposes the cbindgen-generated C ABI as a Clang module.
        .systemLibrary(name: "CScreenBridge", path: "Sources/CScreenBridge"),
        // The SwiftUI app shell (empty window in Phase 0).
        .executableTarget(
            name: "ScreenBridgeApp",
            dependencies: ["CScreenBridge"],
            path: "Sources/ScreenBridgeApp",
            swiftSettings: cInterop,
            linkerSettings: linkCore
        ),
        // Verifies the Swift -> Rust core FFI round-trip without touching the UI.
        .testTarget(
            name: "ScreenBridgeCoreTests",
            dependencies: ["CScreenBridge"],
            path: "Tests/ScreenBridgeCoreTests",
            swiftSettings: cInterop,
            linkerSettings: linkCore
        ),
    ]
)
