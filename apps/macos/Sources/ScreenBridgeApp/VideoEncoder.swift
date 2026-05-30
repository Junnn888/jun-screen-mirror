//  VideoEncoder.swift — hardware H.264 encode (PLAN.md §3 VideoToolbox).
//
//  Wraps a VTCompressionSession configured for low-latency, real-time H.264:
//  EnableLowLatencyRateControl, no B-frames (AllowFrameReordering = false),
//  RealTime, Constrained Baseline. The encoded `CMSampleBuffer` it produces
//  carries a CMVideoFormatDescription (avcC) and is enqueued DIRECTLY on the
//  ASBDL (which decodes internally) — no VTDecompressionSession (WWDC14-513).
//
//  Concurrency: `@unchecked Sendable`. `encode` must only ever be called from
//  the single capture serial queue (this satisfies VideoToolbox's strictly-
//  increasing-PTS / serialised-call contract). VideoToolbox invokes the output
//  handler on its OWN internal thread; that handler is `@Sendable`, so it
//  captures only Sendable values (`feeder`, `stats`, the `UInt64` capture
//  timestamp) — never `self` or the layer — and the freshly-produced encoded
//  buffer is consumed synchronously, crossing no isolation boundary.

import VideoToolbox
import CoreMedia
import CoreVideo

enum VideoEncoderError: Error {
    case sessionCreateFailed(OSStatus)
}

final class VideoEncoder: @unchecked Sendable {
    private let session: VTCompressionSession
    private let feeder: SampleFeeder
    private let stats: LatencyStats

    init(width: Int32, height: Int32, bitrate: Int, feeder: SampleFeeder, stats: LatencyStats) throws {
        self.feeder = feeder
        self.stats = stats

        // Hardware-accelerated, low-latency rate control. HW accel is the macOS
        // default; the spec flag makes the low-latency rate controller explicit.
        let encoderSpec: [CFString: Any] = [
            kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true,
        ]

        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: encoderSpec as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,            // nil ⇒ use EncodeFrameWithOutputHandler
            refcon: nil,
            compressionSessionOut: &created)
        guard status == noErr, let session = created else {
            throw VideoEncoderError.sessionCreateFailed(status)
        }
        self.session = session

        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse) // no B-frames
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_ConstrainedBaseline_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: bitrate))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: 60))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: 120))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: NSNumber(value: 2.0))
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    /// Encode one captured frame. MUST be called from the capture serial queue.
    /// `captureTicks` is the `mach_absolute_time()` stamped when the frame
    /// arrived, threaded through for the latency measurement.
    func encode(_ imageBuffer: CVImageBuffer, pts: CMTime, duration: CMTime, captureTicks: UInt64) {
        // Swift unifies the output-handler form under VTCompressionSessionEncodeFrame
        // with a trailing `outputHandler:` (the `…WithOutputHandler` C name is obsoleted).
        VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: imageBuffer,
            presentationTimeStamp: pts,
            duration: duration,
            frameProperties: nil,
            infoFlagsOut: nil
        ) { [feeder, stats] status, _, sampleBuffer in
            // Runs on a VideoToolbox thread. Captures only Sendable values.
            guard status == noErr, let sampleBuffer else { return }
            stats.recordCaptureToEncoded(sinceTicks: captureTicks)
            feeder.feed(sampleBuffer)
        }
    }

    func invalidate() {
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(session)
    }
}
