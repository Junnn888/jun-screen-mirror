using System;

using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;

namespace ScreenBridge;

/// <summary>
/// Desktop Duplication capture of the PRIMARY display (PLAN.md §3 DDA). Copies
/// each acquired desktop frame — read-only, B8G8R8A8_UNORM, native resolution —
/// into a caller-owned texture, and transparently re-creates the duplication on
/// the ACCESS_LOST that DXGI raises across mode / secure-desktop transitions.
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDXGIOutput1 _output1;
    private IDXGIOutputDuplication _duplication;

    /// <summary>Native pixel width of the primary display.</summary>
    public int Width { get; }
    /// <summary>Native pixel height of the primary display.</summary>
    public int Height { get; }

    public DesktopDuplicator(ID3D11Device device)
    {
        _device = device;

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        _output1 = PickPrimaryOutput1(adapter);

        OutputDescription desc = _output1.Description;
        Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

        _duplication = _output1.DuplicateOutput(_device);
    }

    // The primary monitor is the output whose desktop rect origin is (0,0).
    private static IDXGIOutput1 PickPrimaryOutput1(IDXGIAdapter adapter)
    {
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? output).Success; i++)
        {
            using (output)
            {
                OutputDescription desc = output!.Description;
                if (desc.AttachedToDesktop &&
                    desc.DesktopCoordinates.Left == 0 &&
                    desc.DesktopCoordinates.Top == 0)
                {
                    return output.QueryInterface<IDXGIOutput1>();
                }
            }
        }
        throw new InvalidOperationException("No primary DXGI output found for Desktop Duplication.");
    }

    /// <summary>
    /// Copy the latest desktop frame into <paramref name="dst"/> (same format +
    /// native size). Returns false when no NEW frame is available this tick
    /// (timeout) or the duplication was just re-created — the caller should
    /// re-present the previous contents of <paramref name="dst"/>.
    /// </summary>
    public bool TryCaptureInto(ID3D11DeviceContext context, ID3D11Texture2D dst)
    {
        Result r = _duplication.AcquireNextFrame(16, out OutduplFrameInfo _, out IDXGIResource? resource);
        // Compare on .Code (the proven pattern in R1ProbeWindow.Present handling)
        // rather than relying on a Result==ResultCode operator.
        if (r.Code == ResultCode.WaitTimeout.Code)
        {
            return false;
        }
        if (r.Code == ResultCode.AccessLost.Code || r.Code == ResultCode.AccessDenied.Code)
        {
            Recreate();
            return false;
        }
        r.CheckError();

        try
        {
            using ID3D11Texture2D src = resource!.QueryInterface<ID3D11Texture2D>();
            // The acquired surface is read-only / may be recycled — copy out now.
            context.CopyResource(dst, src);
            return true;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame(); // MUST release before the next AcquireNextFrame
        }
    }

    private void Recreate()
    {
        _duplication.Dispose();
        _duplication = _output1.DuplicateOutput(_device);
    }

    public void Dispose()
    {
        _duplication.Dispose();
        _output1.Dispose();
    }
}
