//! QUIC transport for ScreenBridge (PLAN.md §3, §4.3).
//!
//! Responsibilities (implemented in Phase 2, §8): the `quinn` QUIC connection
//! (ALPN [`screenbridge_protocol::ALPN`]), media transport over unreliable
//! datagrams with the §6.2 framing, the single reliable control stream, and
//! congestion-feedback → target-bitrate hints. Video delta frames are never
//! sent over a reliable stream (§10 R8).
//!
//! Phase 0 is an intentionally empty skeleton so the §5 workspace layout exists
//! and builds; no transport code is written until its phase gate is reached.
