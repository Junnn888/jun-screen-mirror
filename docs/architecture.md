# ScreenBridge — Architecture

> Living document. Authoritative product spec: [`../plans/PLAN.md`](../plans/PLAN.md) §4.
> This summarises the design as built; it expands per phase.

## Component map

```
            HOST device                                   VIEWER device
 ┌──────────────────────────────┐               ┌──────────────────────────────┐
 │ NATIVE                       │               │ NATIVE                       │
 │  Screen capture (SCK/DDA)    │               │  H.264 decode (HW)           │
 │  Sys-audio capture (SCK/WAS) │               │  Audio decode (Opus)         │
 │  H.264 encode (VT/MF, HW)    │               │  Render → normal OS window   │
 │  Audio encode (Opus)         │               │  (Discord-capturable)        │
 │            │  encoded frames  │               │            ▲ encoded frames  │
 │            ▼                  │               │            │                 │
 │  C ABI  ───────────────────  │               │  C ABI  ───────────────────  │
 │            │                  │               │            │                 │
 │ RUST CORE                    │   QUIC/TLS1.3 │ RUST CORE                    │
 │  session · framing · pairing │◀═════════════▶│  session · framing · pairing │
 │  congestion feedback         │   datagrams + │  jitter buffer · feedback    │
 │  mDNS advertise              │   1 ctrl strm │  mDNS browse                 │
 └──────────────────────────────┘               └──────────────────────────────┘
```

## The FFI boundary (critical)

The **encoded bitstream** is the only thing that crosses between native and Rust.

- **Host:** native captures → hardware-encodes → hands encoded packets (+
  metadata) to the Rust core → core frames and sends over QUIC.
- **Viewer:** core receives QUIC datagrams → reassembles frames → hands encoded
  packets to native → native hardware-decodes → renders.

Rust never touches raw pixels or PCM; native never touches the socket. This keeps
the C ABI tiny and the hot path zero-copy on each side.

## Rust core (`core/`)

A Cargo workspace of small crates (PLAN.md §5):

| Crate | Responsibility | Phase |
|---|---|---|
| `protocol` | Wire constants, message types, framing structs, versioning | 0/2 |
| `transport` | quinn QUIC, datagram framing/reassembly, congestion feedback | 2 |
| `session` | State machine, capability negotiation, control channel | 2/3 |
| `security` | SPAKE2 PIN pairing, cert gen/store, key derivation, TOFU | 3 |
| `discovery` | mDNS advertise/browse for `_screenbridge._udp.local` | 2 |
| `ffi` | C ABI (`cbindgen` → `include/screenbridge.h`), panic-safe | 0 |

No `panic` may cross the FFI: every `extern "C"` entry is wrapped in
`catch_unwind` (PLAN.md §6.4, §14).

## Native apps (`apps/`)

- **macOS** (`apps/macos`, SwiftUI/SPM): ScreenCaptureKit capture, VideoToolbox
  encode, `AVSampleBufferDisplayLayer` decode+display, CoreAudio. Links the Rust
  core via a Clang module over `include/screenbridge.h`.
- **Windows** (`apps/windows`, WinUI 3 + Win32/D3D11 viewer): Desktop Duplication
  capture, Media Foundation H.264, D3D11VA decode, WASAPI loopback audio.
  P/Invokes the Rust core via the `ScreenBridge.Interop` library.

## Phase 0 status

Only the skeleton + the C ABI + a trivial "ping" surface (`sb_ping`,
`sb_protocol_version`, `sb_version`) exist today, proven callable from both Swift
and C#. Everything above the FFI line is built in later phases against their DoD
gates (PLAN.md §8).
