import XCTest
import CScreenBridge

/// Verifies the Swift → Rust core C ABI round-trip (PLAN.md §8 Phase 0 DoD:
/// "round-trip ping call works Swift→core"). Calls the cbindgen-generated
/// functions directly, with no UI, so it runs on a headless CI runner.
final class PingTests: XCTestCase {
    func testPingRoundTrip() {
        XCTAssertEqual(sb_ping(41), 42)
        XCTAssertEqual(sb_ping(0), 1)
        XCTAssertEqual(sb_ping(-1), 0)
    }

    func testProtocolVersion() {
        // Matches screenbridge_protocol::PROTOCOL_VERSION (PLAN.md §6.1).
        XCTAssertEqual(sb_protocol_version(), 1)
    }

    func testVersionString() {
        let version = String(cString: sb_version())
        XCTAssertTrue(version.hasPrefix("screenbridge-core "), "unexpected version: \(version)")
    }
}
