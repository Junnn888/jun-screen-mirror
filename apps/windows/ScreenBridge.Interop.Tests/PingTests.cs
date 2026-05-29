using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ScreenBridge.Interop;
using Xunit;

namespace ScreenBridge.Interop.Tests;

/// <summary>
/// Registers a native-library resolver so the <c>screenbridge_ffi</c> cdylib is
/// loaded from the cargo build output regardless of OS. Runs once at assembly
/// load (before any test), so the WinUI app — which deploys the .dll beside its
/// .exe and needs no resolver — is unaffected.
/// </summary>
internal static class NativeResolver
{
    [ModuleInitializer]
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.Library)
            return IntPtr.Zero;

        // 1. Explicit override (used by CI: points at core/target/release/...).
        var overridePath = Environment.GetEnvironmentVariable("SCREENBRIDGE_CORE_LIB");
        if (!string.IsNullOrEmpty(overridePath) &&
            File.Exists(overridePath) &&
            NativeLibrary.TryLoad(overridePath, out var handle))
        {
            return handle;
        }

        // 2. Probe the cargo target dir relative to the repo root.
        var fileName = NativeFileName(libraryName);
        foreach (var profile in new[] { "release", "debug" })
        {
            var candidate = Path.Combine(RepoRoot(), "core", "target", profile, fileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var loaded))
                return loaded;
        }

        // 3. Fall back to default resolution (e.g. .dll beside the exe).
        return IntPtr.Zero;
    }

    private static string NativeFileName(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{name}.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"lib{name}.dylib"
        : $"lib{name}.so";

    private static string RepoRoot()
    {
        // Walk up from the test output dir until we find the folder containing `core`.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "core")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}

/// <summary>
/// Verifies the C# → Rust core C ABI round-trip (PLAN.md §8 Phase 0 DoD:
/// "round-trip ping call works C#→core"). Identical binding path as the WinUI
/// app; only the library-resolution differs.
/// </summary>
public sealed class PingTests
{
    [Fact]
    public void PingRoundTrips()
    {
        Assert.Equal(42, NativeMethods.sb_ping(41));
        Assert.Equal(0, NativeMethods.sb_ping(-1));
        Assert.Equal(1, NativeMethods.sb_ping(0));
    }

    [Fact]
    public void ProtocolVersionMatches()
    {
        // Matches screenbridge_protocol::PROTOCOL_VERSION (PLAN.md §6.1).
        Assert.Equal(1, NativeMethods.sb_protocol_version());
    }

    [Fact]
    public void VersionHasExpectedPrefix()
    {
        Assert.StartsWith("screenbridge-core ", NativeMethods.Version());
    }
}
