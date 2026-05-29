# ScreenBridge — Security Model

> Living document; skeleton at Phase 0, finalised as a full threat model in
> Phase 6. Product spec: [`../plans/PLAN.md`](../plans/PLAN.md) §7. Encryption is
> always on; there is never a plaintext mode (§14).

## Properties we guarantee (target)

- **Confidentiality & integrity:** TLS 1.3 via QUIC on every connection.
- **Authentication & MITM resistance:** the host shows a **6-digit PIN** (and
  QR). Both peers run **SPAKE2** keyed by the PIN over the control stream;
  success proves both know the PIN and binds the session to the peer's
  certificate fingerprint. An attacker without the PIN cannot pair, even on a
  hostile LAN.
- **TOFU:** after first pairing, the peer's certificate fingerprint is stored
  ("remember this device") so reconnects skip the PIN; a changed fingerprint
  raises a loud warning.
- **Least privilege:** view-only (no host input surface), no admin/root; macOS
  hardened runtime + sandbox where possible; no Windows elevation.
- **Privacy:** no telemetry; no network calls beyond the LAN peer and mDNS.

## Supply chain

- Pinned, minimal dependencies; `cargo audit` / `cargo deny` enforced in CI
  (Phase 6 DoD; the workspace already keeps dependencies minimal).
- No secrets, certificates, or keys are ever committed to the repository.

## Robustness

- The datagram/frame parser is fuzzed (Phase 6); reassembly buffers are bounded.
- `catch_unwind` wraps every FFI entry point so no Rust panic crosses the C ABI.

## Known accepted limitations

- DRM-protected content captures as black (OS-enforced). Documented, not fought.

## Phase 0 status

No transport or pairing exists yet. What is already enforced at Phase 0: the C
ABI is panic-safe; no secrets are committed; the build performs no network calls
beyond package registries; the locked "encryption always on" decision is recorded
and will be implemented with the QUIC transport (Phase 2) and SPAKE2 pairing
(Phase 3).
