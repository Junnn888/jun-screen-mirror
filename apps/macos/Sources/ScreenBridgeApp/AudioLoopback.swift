//  AudioLoopback.swift — system audio → Opus → playout (PLAN.md §3, §8 Phase 1).
//
//  Closes the Phase-1 audio loop on one machine: ScreenCaptureKit system-audio
//  buffers are interleaved into exact 20 ms (960-sample @ 48 kHz) Opus frames,
//  Opus-encoded, Opus-decoded, and scheduled for playout on an AVAudioEngine.
//  Opus lives in the native layer (PLAN.md §4) via swift-opus (libopus).
//
//  Concurrency: every codec call and the sample accumulator live on the SCK
//  audio queue (a single serial domain), so the non-Sendable Opus.Encoder/
//  Decoder are never touched concurrently. `@unchecked Sendable` is justified by
//  that confinement; `AVAudioPlayerNode.scheduleBuffer` is itself thread-safe.

import AVFoundation
import CoreMedia
import Opus

final class AudioLoopback: @unchecked Sendable {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let opusFormat: AVAudioFormat          // interleaved Float32 — what Opus requires
    private let playbackFormat: AVAudioFormat      // non-interleaved Float32 — what AVAudioEngine requires
    private let encoder: Opus.Encoder
    private let decoder: Opus.Decoder

    private let frameSamples = 960                 // 20 ms @ 48 kHz (a legal Opus frame)
    private var interleaved: [Float] = []          // L,R,L,R…; touched only on the audio queue
    private var loggedFirstAudio = false

    init() throws {
        // Opus needs INTERLEAVED stereo; AVAudioEngine node connections need
        // NON-interleaved ("standard") — so we deinterleave between decode and playout.
        guard let opus = AVAudioFormat(opusPCMFormat: .float32, sampleRate: 48000, channels: 2),
              let playback = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2) else {
            throw Opus.Error.badArgument
        }
        opusFormat = opus
        playbackFormat = playback
        encoder = try Opus.Encoder(format: opus, application: .audio)
        decoder = try Opus.Decoder(format: opus)
    }

    func startEngine() throws {
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: playbackFormat)
        try engine.start()
        player.play()
    }

    func stop() {
        player.stop()
        engine.stop()
    }

    /// Called on the SCK audio queue.
    func handle(_ sampleBuffer: CMSampleBuffer) {
        appendInterleaved(from: sampleBuffer)
        drainAndPlay()
    }

    // Copy PCM out of the capture buffer (valid only inside the closure) and
    // interleave it into the accumulator. SCK delivers planar Float32.
    private func appendInterleaved(from sampleBuffer: CMSampleBuffer) {
        try? sampleBuffer.withAudioBufferList { audioBufferList, _ in
            guard let first = audioBufferList.first, let firstData = first.mData else { return }

            if audioBufferList.count >= 2, let secondData = audioBufferList[1].mData {
                // Planar: one buffer per channel (the usual SCK layout).
                let frames = Int(first.mDataByteSize) / MemoryLayout<Float>.size
                let left = firstData.assumingMemoryBound(to: Float.self)
                let right = secondData.assumingMemoryBound(to: Float.self)
                interleaved.reserveCapacity(interleaved.count + frames * 2)
                for i in 0..<frames {
                    interleaved.append(left[i])
                    interleaved.append(right[i])
                }
            } else {
                // Single buffer: interleaved stereo or mono.
                let channels = Int(first.mNumberChannels)
                let total = Int(first.mDataByteSize) / MemoryLayout<Float>.size
                let samples = firstData.assumingMemoryBound(to: Float.self)
                if channels >= 2 {
                    interleaved.append(contentsOf: UnsafeBufferPointer(start: samples, count: total))
                } else {
                    for i in 0..<total {                 // mono → duplicate to stereo
                        interleaved.append(samples[i])
                        interleaved.append(samples[i])
                    }
                }
            }

            if !loggedFirstAudio {
                loggedFirstAudio = true
                print("[ScreenBridge] first system-audio buffer captured — Opus round-tripping")
            }
        }
    }

    private func drainAndPlay() {
        let chunkFloats = frameSamples * 2   // interleaved stereo
        while interleaved.count >= chunkFloats {
            guard let pcm = AVAudioPCMBuffer(pcmFormat: opusFormat, frameCapacity: AVAudioFrameCount(frameSamples)),
                  let dst = pcm.floatChannelData?[0] else { break }
            pcm.frameLength = AVAudioFrameCount(frameSamples)
            interleaved.withUnsafeBufferPointer { dst.update(from: $0.baseAddress!, count: chunkFloats) }
            interleaved.removeFirst(chunkFloats)

            do {
                var encoded = Data(count: 4000)                    // ample for a 20 ms Opus frame
                let n = try encoder.encode(pcm, to: &encoded)      // sets encoded.count = n
                guard n > 0 else { continue }
                let decoded = try decoder.decode(encoded)          // → interleaved Float32

                // Deinterleave into a non-interleaved buffer for the engine.
                guard let out = AVAudioPCMBuffer(pcmFormat: playbackFormat, frameCapacity: AVAudioFrameCount(frameSamples)),
                      let src = decoded.floatChannelData?[0],
                      let dstL = out.floatChannelData?[0],
                      let dstR = out.floatChannelData?[1] else { continue }
                let frames = min(Int(decoded.frameLength), frameSamples)
                out.frameLength = AVAudioFrameCount(frames)
                for i in 0..<frames {
                    dstL[i] = src[2 * i]
                    dstR[i] = src[2 * i + 1]
                }
                player.scheduleBuffer(out, completionHandler: nil)
            } catch {
                print("[ScreenBridge] opus round-trip error: \(error)")
            }
        }
    }
}
