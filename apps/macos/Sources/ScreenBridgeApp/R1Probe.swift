//  R1Probe.swift — Phase 1 viewer surface + local loopback wiring (PLAN.md §8).
//
//  R1 (the make-or-break gate) is PROVEN: this AVSampleBufferDisplayLayer (ASBDL)
//  surface in a normal NSWindow is window-capturable by Discord/OBS. With that
//  established, the synthetic frame source has been replaced by the REAL local
//  loopback: ScreenCaptureKit captures the primary display → VideoToolbox
//  hardware-encodes H.264 → the encoded sample is enqueued straight onto this
//  ASBDL, which decodes + displays internally (no VTDecompressionSession). The
//  capture→encode→display path is built in DisplayCapturer / VideoEncoder /
//  DisplaySink; this file is just the SwiftUI host that owns the layer and
//  starts/stops the pipeline.
//
//  Concurrency (Swift 6 strict): the layer is created/owned on the main actor
//  (DisplaySink, @MainActor); frames are pushed in off-main via a Sendable
//  SampleFeeder. No non-Sendable buffer crosses an isolation boundary.

import SwiftUI
import AppKit
import AVFoundation
import QuartzCore

// MARK: - SwiftUI bridge

/// Hosts the ASBDL viewer surface inside SwiftUI. The window that contains this
/// view is the artefact Discord/OBS capture; it now shows the live mirror.
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
        private let pipeline = LoopbackPipeline()
        private var sink: DisplaySink?

        func start(target layer: AVSampleBufferDisplayLayer) {
            let sink = DisplaySink(layer: layer)
            self.sink = sink
            let feeder = sink.makeFeeder()              // Sendable; built on the main actor
            let pipeline = self.pipeline                // Sendable; capture locally, not `self`
            Task.detached {
                do {
                    try await pipeline.start(feeder: feeder)
                } catch {
                    print("[ScreenBridge] capture pipeline failed to start: \(error)\n" +
                          "Grant Screen Recording in System Settings → Privacy & Security → " +
                          "Screen & System Audio Recording, then relaunch.")
                }
            }
        }

        func stop() { pipeline.stop() }
    }
}

/// An `NSView` whose backing layer IS an `AVSampleBufferDisplayLayer`, so the
/// decoded frames are composited by the window server exactly as proven in R1.
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
