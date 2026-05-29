//! Pairing and transport security for ScreenBridge (PLAN.md §3, §7).
//!
//! Responsibilities (Phase 3, §8): SPAKE2 PAKE keyed by a 6-digit PIN over the
//! control stream, self-signed QUIC certificate generation, key derivation, and
//! the trust-on-first-use ("remember this device") fingerprint store with a
//! loud warning on fingerprint change. Encryption is always on (§14); there is
//! never a plaintext mode.
//!
//! Phase 0 is an intentionally empty skeleton; no security logic is written
//! until its phase gate is reached. No secrets, certs or keys are ever committed.
