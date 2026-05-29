using System.Runtime.InteropServices;

namespace ScreenBridge.Interop;

/// <summary>
/// P/Invoke bindings for the ScreenBridge Rust core C ABI
/// (see <c>include/screenbridge.h</c>, PLAN.md §6.4).
/// </summary>
/// <remarks>
/// The native library base name is <c>screenbridge_ffi</c>; the runtime resolves
/// it per-platform to <c>screenbridge_ffi.dll</c> (Windows),
/// <c>libscreenbridge_ffi.dylib</c> (macOS) or <c>libscreenbridge_ffi.so</c>
/// (Linux). For the WinUI 3 app the .dll is deployed beside the executable; the
/// interop tests register a <see cref="System.Runtime.InteropServices.NativeLibrary"/>
/// resolver pointing at the cargo build output.
/// </remarks>
public static partial class NativeMethods
{
    /// <summary>Native library base name (no prefix/suffix); used by <c>[LibraryImport]</c>.</summary>
    public const string Library = "screenbridge_ffi";

    /// <summary>
    /// Round-trip "ping": returns <paramref name="value"/> + 1 (wrapping).
    /// PLAN.md §6.4 / Phase 0 liveness check.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int sb_ping(int value);

    /// <summary>Locked wire-protocol major version (PLAN.md §6.1).</summary>
    [LibraryImport(Library)]
    public static partial ushort sb_protocol_version();

    /// <summary>
    /// Borrowed pointer to a static, NUL-terminated UTF-8 version string. The
    /// caller must NOT free it (PLAN.md §6.4 ownership rules).
    /// </summary>
    [LibraryImport(Library)]
    public static partial IntPtr sb_version();

    /// <summary>Marshals <see cref="sb_version"/> into a managed string.</summary>
    public static string Version()
    {
        IntPtr ptr = sb_version();
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }
}
