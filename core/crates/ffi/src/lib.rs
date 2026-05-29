//! C ABI surface for the ScreenBridge core (PLAN.md §6.4).
//!
//! Invariants honoured here (PLAN.md §3, §6.4, §14):
//! - No Rust `panic` may cross the C ABI: every `extern "C"` entry point runs
//!   its body inside [`std::panic::catch_unwind`].
//! - Returned strings are UTF-8, NUL-terminated, with documented ownership.
//!
//! Phase 0 exposes only a trivial round-trip ("ping"), a borrowed version
//! string, and the locked protocol version — just enough to prove the Swift and
//! C# bindings reach the Rust core end to end. The full media/session ABI from
//! §6.4 arrives in later phases.

use std::ffi::c_char;
use std::panic::catch_unwind;

use screenbridge_protocol::PROTOCOL_VERSION;

/// Core version string, NUL-terminated for direct C ABI exposure (`'static`).
static VERSION_C: &str = concat!("screenbridge-core ", env!("CARGO_PKG_VERSION"), "\0");

/// Round-trip "ping": returns `value + 1` (wrapping).
///
/// This is the Phase 0 liveness check proving a native caller (Swift/C#) can
/// invoke the Rust core across the C ABI and observe the returned value. The
/// body cannot panic; the [`catch_unwind`] guard applies the §14 "no panic may
/// cross the FFI" rule uniformly and yields `INT32_MIN` in the unreachable
/// panic case.
#[no_mangle]
pub extern "C" fn sb_ping(value: i32) -> i32 {
    catch_unwind(|| value.wrapping_add(1)).unwrap_or(i32::MIN)
}

/// Returns the locked wire-protocol major version (PLAN.md §6.1).
///
/// Lets native UIs surface/negotiate the protocol version, and proves the FFI
/// crate links the rest of the Rust workspace. Returns `0` only in the
/// unreachable panic case.
#[no_mangle]
pub extern "C" fn sb_protocol_version() -> u16 {
    catch_unwind(|| PROTOCOL_VERSION).unwrap_or(0)
}

/// Returns a pointer to a static, NUL-terminated UTF-8 version string.
///
/// Ownership: the returned pointer is **borrowed**, has `'static` lifetime, and
/// must NOT be freed by the caller. Returns `NULL` only in the unreachable panic
/// case.
#[no_mangle]
pub extern "C" fn sb_version() -> *const c_char {
    catch_unwind(|| VERSION_C.as_ptr().cast::<c_char>()).unwrap_or(std::ptr::null())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CStr;

    #[test]
    fn ping_increments() {
        assert_eq!(sb_ping(41), 42);
        assert_eq!(sb_ping(0), 1);
        assert_eq!(sb_ping(-1), 0);
        // Wrapping arithmetic never panics, even at the boundary.
        assert_eq!(sb_ping(i32::MAX), i32::MIN);
    }

    #[test]
    fn protocol_version_matches() {
        assert_eq!(sb_protocol_version(), PROTOCOL_VERSION);
    }

    #[test]
    fn version_is_valid_cstr() {
        let ptr = sb_version();
        assert!(!ptr.is_null());
        // SAFETY: sb_version returns a 'static NUL-terminated UTF-8 string.
        let s = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap();
        assert!(s.starts_with("screenbridge-core "));
    }
}
