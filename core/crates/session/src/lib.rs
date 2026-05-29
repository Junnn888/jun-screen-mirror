//! Session orchestration for ScreenBridge (PLAN.md §4.3).
//!
//! Responsibilities (Phase 2/3, §8): the connected → paired → streaming → error
//! state machine, capability negotiation (codec/resolution/fps/audio), the
//! control-message handling (§6.3), keyframe-request signalling and stats.
//!
//! Phase 0 is an intentionally empty skeleton; no session logic is written until
//! its phase gate is reached.
