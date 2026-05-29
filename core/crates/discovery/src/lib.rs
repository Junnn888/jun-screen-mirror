//! LAN discovery for ScreenBridge (PLAN.md §3, §4.3).
//!
//! Responsibilities (Phase 2, §8): advertise and browse the
//! `_screenbridge._udp.local` mDNS service via `mdns-sd` so hosts appear
//! automatically on the LAN, with manual IP + PIN entry as the documented
//! fallback for client-isolated Wi-Fi (§10 R7).
//!
//! Phase 0 is an intentionally empty skeleton; no discovery logic is written
//! until its phase gate is reached.
