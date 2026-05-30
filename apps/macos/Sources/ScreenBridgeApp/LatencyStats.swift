//  LatencyStats.swift — Phase 1 local latency instrumentation (PLAN.md §8/§9).
//
//  Measures **capture→encoded** latency: from the instant a frame arrives in the
//  ScreenCaptureKit callback to the instant VideoToolbox hands back the encoded
//  H.264 sample. This is the honest local number we can measure on one machine;
//  it deliberately EXCLUDES the ASBDL-internal decode and the display scan-out
//  (~8–16 ms at 60 Hz), and a loopback shares one GPU/panel — so it is NOT a
//  true glass-to-glass figure (PLAN.md §9 caveat). A 240 fps phone-video check
//  remains the way to capture the scan-out tail (docs/r1-verification.md style).
//
//  Thread-safety: `record` is called from the VideoToolbox output thread; the
//  class is `@unchecked Sendable` because every mutable field is guarded by
//  `lock` — that lock is the manual proof the unchecked opt-out requires.

import Foundation
import Darwin

final class LatencyStats: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    private var sumNanos: UInt64 = 0
    private var minNanos: UInt64 = .max
    private var maxNanos: UInt64 = 0

    // mach tick → nanosecond conversion factor; cached once (cheap thereafter).
    private static let timebase: mach_timebase_info_data_t = {
        var tb = mach_timebase_info_data_t()
        mach_timebase_info(&tb)
        return tb
    }()

    /// Record one frame's capture→encoded latency. `startTicks` is the
    /// `mach_absolute_time()` stamped in the capture callback; "now" is the
    /// encode-completion moment. Logs a rolling summary every 120 frames.
    func recordCaptureToEncoded(sinceTicks startTicks: UInt64) {
        let elapsedTicks = mach_absolute_time() &- startTicks
        let tb = Self.timebase
        let nanos = elapsedTicks &* UInt64(tb.numer) / UInt64(tb.denom)

        lock.lock()
        count += 1
        sumNanos &+= nanos
        if nanos < minNanos { minNanos = nanos }
        if nanos > maxNanos { maxNanos = nanos }
        let shouldLog = count % 120 == 0
        let snapCount = count
        let snapAvg = sumNanos / UInt64(count)
        let snapMin = minNanos
        let snapMax = maxNanos
        lock.unlock()

        if shouldLog {
            let ms = { (n: UInt64) in Double(n) / 1_000_000 }
            print(String(
                format: "[ScreenBridge] capture→encoded latency over %d frames: avg %.2f ms, min %.2f ms, max %.2f ms (excludes decode + display scan-out)",
                snapCount, ms(snapAvg), ms(snapMin), ms(snapMax)))
        }
    }
}
