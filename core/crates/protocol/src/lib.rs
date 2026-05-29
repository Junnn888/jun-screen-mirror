//! Shared wire-protocol constants and message types for ScreenBridge.
//!
//! The authoritative wire specification lives in [`docs/protocol.md`]; this
//! crate is the single Rust source of truth for the values that both peers and
//! every other core crate must agree on. Phase 0 defines only the handful of
//! constants that are already locked by [PLAN.md §6.1]; the framing structs and
//! control-message enum (§6.2, §6.3) arrive in Phase 2 alongside the transport.
//!
//! [`docs/protocol.md`]: ../../../docs/protocol.md

/// QUIC ALPN identifier negotiated for every ScreenBridge session (PLAN.md §6.1).
///
/// One QUIC connection per session advertises exactly this protocol; peers that
/// do not offer it are rejected at the TLS layer.
pub const ALPN: &[u8] = b"screenbridge/1";

/// Wire-protocol major version (PLAN.md §6). Unknown major versions are rejected
/// during the `Hello` capability exchange.
pub const PROTOCOL_VERSION: u16 = 1;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn alpn_matches_locked_value() {
        // Guards against an accidental rename of the on-the-wire protocol id.
        assert_eq!(ALPN, b"screenbridge/1");
        assert_eq!(PROTOCOL_VERSION, 1);
    }
}
