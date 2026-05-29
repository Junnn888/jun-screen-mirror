//  R1Probe.swift — Phase 1, Risk R1 capture-probe (PLAN.md §10 R1, §8 Phase 1).
//
//  R1 is the make-or-break gate: the viewer window must be selectable AND show
//  live, moving content in Discord/OBS window-capture. This file builds the
//  REAL viewer render surface — an `AVSampleBufferDisplayLayer` (ASBDL) hosted
//  in a normal `NSWindow` — and drives it with synthetic, animated, IOSurface-
//  backed frames. It deliberately exercises the same GPU-composited display
//  path the live H.264 pipeline will use (ASBDL + IOSurface), WITHOUT the
//  capture/encode/decode chain, so that capturability is proven in isolation
//  before the rest of Phase 1 is built on top of it (per the "prove R1 first"
//  mandate). The synthetic feed is swapped for decoded H.264 once R1 passes.
//
//  Concurrency: everything here lives on the main actor. `CVPixelBuffer` /
//  `CMSampleBuffer` are non-Sendable and never cross an isolation boundary —
//  the render loop is a `@MainActor` task, so the Swift 6 strict-concurrency
//  build stays clean without weakening isolation (PLAN.md §3 toolchain lock).

import SwiftUI
import AppKit
import AVFoundation
import CoreMedia
import CoreVideo
import QuartzCore

// MARK: - SwiftUI bridge

/// Hosts the ASBDL viewer surface inside SwiftUI. The window that contains this
/// view is the artefact R1 verifies in Discord/OBS.
struct R1ProbeView: NSViewRepresentable {
    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> SampleBufferHostView {
        let view = SampleBufferHostView(frame: NSRect(x: 0, y: 0, width: 1280, height: 720))
        context.coordinator.start(target: view.displayLayer)
        return view
    }

    func updateNSView(_ nsView: SampleBufferHostView, context: Context) {}

    static func dismantleNSView(_ nsView: SampleBufferHostView, coordinator: Coordinator) {
        coordinator.stop()
    }

    @MainActor
    final class Coordinator {
        private let generator = R1FrameGenerator()
        func start(target layer: AVSampleBufferDisplayLayer) { generator.start(target: layer) }
        func stop() { generator.stop() }
    }
}

/// An `NSView` whose backing layer IS an `AVSampleBufferDisplayLayer`, so the
/// decoded/synthetic frames are composited by the window server exactly as the
/// live pipeline will composite real video.
final class SampleBufferHostView: NSView {
    override func makeBackingLayer() -> CALayer {
        let layer = AVSampleBufferDisplayLayer()
        layer.videoGravity = .resizeAspect
        layer.backgroundColor = NSColor.black.cgColor
        return layer
    }

    var displayLayer: AVSampleBufferDisplayLayer {
        // Safe: `wantsLayer = true` forces `makeBackingLayer()` to run, so the
        // backing layer is always our ASBDL.
        layer as! AVSampleBufferDisplayLayer
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layerContentsRedrawPolicy = .duringViewResize
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
}

// MARK: - Synthetic animated frame source

/// Generates ~60 fps of animated, IOSurface-backed BGRA frames and enqueues
/// them on an ASBDL for immediate display. Pure R1 scaffolding: it proves the
/// surface is live and capturable; it is not the production frame source.
@MainActor
final class R1FrameGenerator {
    private weak var layer: AVSampleBufferDisplayLayer?
    private var renderLoop: Task<Void, Never>?
    private var pool: CVPixelBufferPool?
    private var frameIndex: Int64 = 0

    private let width = 1280
    private let height = 720
    private let frameRate: Int32 = 60

    func start(target layer: AVSampleBufferDisplayLayer) {
        self.layer = layer
        pool = Self.makePool(width: width, height: height)
        renderLoop = Task { @MainActor [weak self] in
            await self?.run()
        }
    }

    func stop() {
        renderLoop?.cancel()
        renderLoop = nil
    }

    private func run() async {
        let frameInterval = UInt64(1_000_000_000 / UInt64(frameRate))
        while !Task.isCancelled {
            tick()
            try? await Task.sleep(nanoseconds: frameInterval)
        }
    }

    private func tick() {
        guard let layer, let pool else { return }

        // ASBDL recovers from a transient failure by flushing.
        if layer.status == .failed { layer.flush() }
        guard layer.isReadyForMoreMediaData else { return }

        guard let pixelBuffer = makeFrame(pool: pool) else { return }
        guard let sampleBuffer = Self.wrap(pixelBuffer, frameIndex: frameIndex, frameRate: frameRate) else { return }
        layer.enqueue(sampleBuffer)
        frameIndex &+= 1
    }

    // MARK: drawing

    private func makeFrame(pool: CVPixelBufferPool) -> CVPixelBuffer? {
        var pixelBuffer: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &pixelBuffer) == kCVReturnSuccess,
              let pixelBuffer else { return nil }

        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let bitmapInfo = CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        guard let ctx = CGContext(data: base,
                                  width: width,
                                  height: height,
                                  bitsPerComponent: 8,
                                  bytesPerRow: bytesPerRow,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: bitmapInfo) else { return nil }

        let w = CGFloat(width), h = CGFloat(height)

        // Cycling background so even a static screen reads as "live".
        let hue = CGFloat(frameIndex % 360) / 360.0
        ctx.setFillColor(NSColor(hue: hue, saturation: 0.55, brightness: 0.28, alpha: 1).cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))

        // A box that sweeps left↔right — unmistakable motion for the capturer.
        let span = w - 160
        let phase = CGFloat((frameIndex % 240)) / 240.0
        let triangle = phase < 0.5 ? phase * 2 : (1 - phase) * 2   // 0→1→0
        let boxX = 40 + span * triangle
        ctx.setFillColor(NSColor.systemYellow.cgColor)
        ctx.fill(CGRect(x: boxX, y: h / 2 - 60, width: 120, height: 120))

        // Large monotonically-changing readout (also handy for eyeballing
        // latency between the source window and a captured copy).
        let nsContext = NSGraphicsContext(cgContext: ctx, flipped: false)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = nsContext
        let text = "ScreenBridge — R1 probe\nframe \(frameIndex)"
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 64, weight: .bold),
            .foregroundColor: NSColor.white,
        ]
        (text as NSString).draw(at: NSPoint(x: 48, y: h - 180), withAttributes: attrs)
        NSGraphicsContext.restoreGraphicsState()

        return pixelBuffer
    }

    // MARK: helpers

    private static func makePool(width: Int, height: Int) -> CVPixelBufferPool? {
        let pixelBufferAttrs: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
            // Empty dict requests IOSurface backing — the GPU-shareable surface
            // the window server composites and the capturer reads (the R1 risk).
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any]() as CFDictionary,
            kCVPixelBufferCGBitmapContextCompatibilityKey as String: true,
        ]
        let poolAttrs: [String: Any] = [kCVPixelBufferPoolMinimumBufferCountKey as String: 4]
        var pool: CVPixelBufferPool?
        CVPixelBufferPoolCreate(kCFAllocatorDefault, poolAttrs as CFDictionary, pixelBufferAttrs as CFDictionary, &pool)
        return pool
    }

    private static func wrap(_ pixelBuffer: CVPixelBuffer, frameIndex: Int64, frameRate: Int32) -> CMSampleBuffer? {
        var formatDescription: CMVideoFormatDescription?
        guard CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault,
                                                            imageBuffer: pixelBuffer,
                                                            formatDescriptionOut: &formatDescription) == noErr,
              let formatDescription else { return nil }

        var timing = CMSampleTimingInfo(
            duration: CMTime(value: 1, timescale: frameRate),
            presentationTimeStamp: CMTime(value: frameIndex, timescale: frameRate),
            decodeTimeStamp: .invalid)

        var sampleBuffer: CMSampleBuffer?
        guard CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault,
                                                       imageBuffer: pixelBuffer,
                                                       formatDescription: formatDescription,
                                                       sampleTiming: &timing,
                                                       sampleBufferOut: &sampleBuffer) == noErr,
              let sampleBuffer else { return nil }

        // Display each frame the moment it is enqueued (live, low-latency path)
        // rather than scheduling against a control timebase.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true) as? [NSMutableDictionary] {
            attachments.first?[kCMSampleAttachmentKey_DisplayImmediately as String] = true
        }
        return sampleBuffer
    }
}
