import SwiftUI
import CScreenBridge

/// ScreenBridge macOS app shell (PLAN.md §8 Phase 0).
///
/// Phase 0 is an empty window plus a Swift → Rust core FFI liveness check on
/// launch. Screen capture, hardware encode and the AVSampleBufferDisplayLayer
/// render path (PLAN.md §3) arrive in Phase 1; nothing else is built here.
///
/// NOTE: this file must NOT be named `main.swift` — `@main` and top-level code
/// are mutually exclusive in an SPM executable target.
@main
struct ScreenBridgeApp: App {
    init() {
        // Prove the Swift → Rust core C ABI round-trip at launch. The
        // deterministic verification lives in the XCTest target so headless CI
        // never needs a WindowServer.
        let pong = sb_ping(41)
        let version = String(cString: sb_version())
        let proto = sb_protocol_version()
        print("[ScreenBridge] core ping: sb_ping(41)=\(pong), version=\"\(version)\", protocol=v\(proto)")
    }

    var body: some Scene {
        WindowGroup("ScreenBridge") {
            ContentView()
        }
    }
}

private struct ContentView: View {
    var body: some View {
        // Empty shell window for Phase 0.
        Text("ScreenBridge")
            .frame(minWidth: 480, minHeight: 320)
    }
}
