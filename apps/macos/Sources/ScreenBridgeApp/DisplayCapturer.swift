//  DisplayCapturer.swift — ScreenCaptureKit primary-display capture (PLAN.md §3).
//
//  Captures the PRIMARY display at 1080p60, cursor included, as NV12
//  (kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange) IOSurface-backed pixel
//  buffers — the native input format for the VideoToolbox encoder, so no
//  colour conversion is needed. System-audio capture is added in the next
//  increment. Requesting `SCShareableContent` is what triggers the macOS
//  Screen Recording (TCC) permission prompt; denial surfaces as a thrown error.
//
//  Concurrency: the `SCStreamOutput` callback is `nonisolated` and delivered on
//  the serial `captureQueue`; that queue is the synchronisation domain (no
//  actor). The non-Sendable `CMSampleBuffer`/`CVImageBuffer` never leave it —
//  the encoder's `encode` runs synchronously on this queue.

import ScreenCaptureKit
import CoreMedia
import CoreVideo
import CoreGraphics
import Darwin

enum CaptureError: Error {
    case noDisplay
}

/// Orchestrates the local video loopback: capture → encode → ASBDL. Owns the
/// shared `LatencyStats`. `@unchecked Sendable`: `start` runs on a background
/// task and `stop` on the main actor, but they touch `capturer` only at
/// setup/teardown, not concurrently with the hot path.
final class LoopbackPipeline: @unchecked Sendable {
    private let stats = LatencyStats()
    private var capturer: DisplayCapturer?

    func start(feeder: SampleFeeder) async throws {
        let encoder = try VideoEncoder(width: 1920, height: 1080, bitrate: 8_000_000, feeder: feeder, stats: stats)
        let audio = try AudioLoopback()
        try audio.startEngine()
        let capturer = DisplayCapturer(encoder: encoder, audio: audio)
        self.capturer = capturer
        try await capturer.start()
    }

    func stop() {
        capturer?.stop()
        capturer = nil
    }
}

final class DisplayCapturer: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let captureQueue = DispatchQueue(label: "com.screenbridge.capture", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "com.screenbridge.audio", qos: .userInitiated)
    private let encoder: VideoEncoder
    private let audio: AudioLoopback
    private var stream: SCStream?
    private var loggedFirstFrame = false   // touched only on captureQueue

    init(encoder: VideoEncoder, audio: AudioLoopback) {
        self.encoder = encoder
        self.audio = audio
        super.init()
    }

    func start() async throws {
        // Requesting shareable content triggers the Screen Recording TCC prompt.
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else {
            throw CaptureError.noDisplay
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])

        let config = SCStreamConfiguration()
        config.width = 1920
        config.height = 1080
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)   // cap at 60 fps
        config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange // NV12 / '420v'
        config.showsCursor = true                                       // cursor included
        config.scalesToFit = true                                       // downscale Retina to 1080p
        config.queueDepth = 5
        // System audio (PLAN.md §3 WASAPI-equivalent path), 48 kHz stereo for Opus.
        config.capturesAudio = true
        config.sampleRate = 48000
        config.channelCount = 2
        if #available(macOS 14.0, *) {
            // Exclude our OWN playout from capture so the audio loopback can't
            // feed back on itself on a single machine. (macOS 14+ only.)
            config.excludesCurrentProcessAudio = true
        }

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: captureQueue)
        try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        self.stream = stream

        try await stream.startCapture()
        print("[ScreenBridge] capture started: \(display.width)×\(display.height) display → 1080p60 H.264 + Opus audio loopback")
    }

    func stop() {
        stream?.stopCapture(completionHandler: { _ in })
        stream = nil
        encoder.invalidate()
        audio.stop()
    }

    // MARK: SCStreamOutput (.screen on captureQueue, .audio on audioQueue)

    nonisolated func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        if type == .audio {
            audio.handle(sampleBuffer)
            return
        }
        guard type == .screen else { return }

        // Skip only frames we can POSITIVELY identify as non-complete (e.g. SCK
        // .idle when nothing changed). If the attachment can't be parsed we still
        // display the frame, so a parsing quirk can't silently black the window.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let statusRaw = attachments.first?[.status] as? Int,
           let frameStatus = SCFrameStatus(rawValue: statusRaw),
           frameStatus != .complete {
            return
        }

        guard let imageBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        if !loggedFirstFrame {
            loggedFirstFrame = true
            print("[ScreenBridge] first frame captured — encoding + displaying")
        }

        let captureTicks = mach_absolute_time()
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let duration = CMSampleBufferGetDuration(sampleBuffer)
        encoder.encode(imageBuffer, pts: pts, duration: duration, captureTicks: captureTicks)
    }

    // MARK: SCStreamDelegate

    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        print("[ScreenBridge] capture stopped with error: \(error)")
    }
}
