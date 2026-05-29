import SwiftUI
import AppKit
import CScreenBridge

/// ScreenBridge macOS app shell.
///
/// Phase 0 was an empty window plus a Swift → Rust core FFI liveness check on
/// launch. Phase 1 (this build) hosts the Risk-R1 capture-probe: the real
/// `AVSampleBufferDisplayLayer` viewer surface (PLAN.md §3) driven by synthetic
/// animated frames, so its capturability in Discord/OBS is proven before the
/// capture→encode→decode pipeline is built on top (PLAN.md §10 R1). See
/// `R1Probe.swift`.
///
/// NOTE: this file must NOT be named `main.swift` — `@main` and top-level code
/// are mutually exclusive in an SPM executable target.
@main
struct ScreenBridgeApp: App {
    // A bare SPM executable launches without a regular activation policy, so its
    // window would not behave as (or be reliably capturable as) a normal app
    // window. The delegate promotes it to a regular, focusable, front app.
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

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

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }
}

private struct ContentView: View {
    var body: some View {
        // The R1 capture-probe surface fills the window edge-to-edge so a
        // window-capture of this window shows the live viewer content.
        R1ProbeView()
            .frame(minWidth: 640, minHeight: 360)
            .frame(idealWidth: 960, idealHeight: 540)
    }
}
