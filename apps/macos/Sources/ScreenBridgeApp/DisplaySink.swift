//  DisplaySink.swift — the viewer render surface seam (PLAN.md §3, §10 R1).
//
//  `DisplaySink` owns the AVSampleBufferDisplayLayer (ASBDL) and is `@MainActor`
//  because CALayer creation/geometry must stay on the main actor. The encoded
//  frames, however, arrive on the VideoToolbox output thread, so the layer is
//  fed through a `SampleFeeder`: a tiny `@unchecked Sendable` shim exposing ONLY
//  the members that are documented-safe to call off the main thread
//  (`enqueue`, `status`, `flush`, `isReadyForMoreMediaData`). On macOS 13 these
//  are the correct, non-deprecated calls — the thread-safe `sampleBufferRenderer`
//  is macOS 14+ and must not be used here.

import AVFoundation
import CoreMedia
import QuartzCore

@MainActor
final class DisplaySink {
    let layer: AVSampleBufferDisplayLayer

    init(layer: AVSampleBufferDisplayLayer) {
        self.layer = layer
    }

    /// Produce the Sendable feeder the off-main encoder pushes frames into.
    /// Built on the main actor (the only place the layer may be touched for
    /// setup); thereafter only `SampleFeeder`'s safe surface is used off-main.
    func makeFeeder() -> SampleFeeder {
        SampleFeeder(layer: layer)
    }
}

/// Off-main bridge to the ASBDL. `@unchecked Sendable` is justified because it
/// calls only the layer members that are safe off the main thread, and never
/// mutates layer geometry. The encoded `CMSampleBuffer` is consumed synchronously
/// here (no isolation boundary is crossed), so it need not be Sendable.
final class SampleFeeder: @unchecked Sendable {
    private let layer: AVSampleBufferDisplayLayer

    init(layer: AVSampleBufferDisplayLayer) {
        self.layer = layer
    }

    func feed(_ sampleBuffer: CMSampleBuffer) {
        if layer.status == .failed { layer.flush() }
        guard layer.isReadyForMoreMediaData else { return }   // back-pressure: drop if not ready

        // Display the moment it is enqueued (live low-latency path) rather than
        // scheduling against a control timebase.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true) as? [NSMutableDictionary] {
            attachments.first?[kCMSampleAttachmentKey_DisplayImmediately as String] = true
        }
        layer.enqueue(sampleBuffer)
    }
}
