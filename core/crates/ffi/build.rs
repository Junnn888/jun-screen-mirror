//! Build script: generate the C ABI header with cbindgen.
//!
//! Writes `<repo>/include/screenbridge.h` from this crate's public `extern "C"`
//! surface so that the macOS (Swift/Clang module) and Windows apps consume a
//! single, generated source of truth (PLAN.md §5, §6.4, Phase 0 DoD).

use std::env;
use std::path::PathBuf;

fn main() {
    let crate_dir = PathBuf::from(
        env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR is always set by cargo"),
    );

    // <repo>/core/crates/ffi → ancestors: [ffi, crates, core, <repo>, ...].
    let repo_root = crate_dir
        .ancestors()
        .nth(3)
        .expect("ffi crate must live at <repo>/core/crates/ffi")
        .to_path_buf();

    let include_dir = repo_root.join("include");
    std::fs::create_dir_all(&include_dir)
        .unwrap_or_else(|e| panic!("could not create {}: {e}", include_dir.display()));
    let header_path = include_dir.join("screenbridge.h");

    // Regenerate when the ABI source or the cbindgen config changes.
    println!("cargo:rerun-if-changed=src/lib.rs");
    println!("cargo:rerun-if-changed=cbindgen.toml");

    // Header generation is load-bearing (Phase 0 DoD): fail the build LOUDLY if
    // cbindgen cannot emit the C ABI header, rather than warning and shipping a
    // stale/committed one. `cargo build` itself is therefore the real gate.
    let config = cbindgen::Config::from_root_or_default(&crate_dir);
    let bindings = cbindgen::Builder::new()
        .with_crate(&crate_dir)
        .with_config(config)
        .generate()
        .unwrap_or_else(|e| panic!("cbindgen failed to generate {}: {e}", header_path.display()));
    bindings.write_to_file(&header_path);
}
