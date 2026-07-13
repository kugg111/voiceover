using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using SharpDXDevice = SharpDX.Direct3D11.Device;
using SharpDXTexture2D = SharpDX.Direct3D11.Texture2D;

namespace Voiceover.Client.Services;

// Raw WinRT/COM interop glue for Windows.Graphics.Capture, adapted from
// Microsoft's own WPF screen capture sample (Direct3D11Helper.cs +
// CaptureHelper.cs in microsoft/Windows.UI.Composition-Win32-Samples) - this
// isn't projected into a friendly .NET API, so bridging IDirect3DDevice/
// IDirect3DSurface (WinRT) to SharpDX.Direct3D11.Device/Texture2D (native
// COM) needs the same manual QueryInterface/CreateDirect3D11* dance the
// original sample uses. SharpDX is unmaintained upstream but still a plain
// managed COM-interop assembly with no .NET Framework-specific behavior, so
// it works fine on net8.0-windows - kept for parity with the proven-correct
// reference implementation rather than hand-adapting to a different (and,
// for this exact interop path, unverified) D3D11 binding.
internal static class Direct3D11Interop
{
    private static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // WinRT-projected objects (a GraphicsCapturePicker, an IDirect3DSurface)
    // are not classic COM RCWs under modern CsWinRT - a plain C# cast to a
    // hand-declared ComImport interface throws InvalidCastException instead
    // of doing a real QueryInterface. `.As<T>()` (from the `WinRT`
    // namespace) is CsWinRT's own supported replacement for that cast.
    // Wrapping a raw native pointer we already own into a projected type
    // goes through MarshalInterface<T>.FromAbi instead of
    // Marshal.GetObjectForIUnknown+cast for the same reason - and FromAbi
    // takes ownership of the ref we pass it, so (unlike GetObjectForIUnknown)
    // there's no separate Marshal.Release after.

    public static IDirect3DDevice CreateDirect3DDeviceFromSharpDXDevice(SharpDXDevice d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device3>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pUnknown);
        if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");

        return MarshalInterface<IDirect3DDevice>.FromAbi(pUnknown);
    }

    public static SharpDXTexture2D CreateSharpDXTexture2D(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var pointer = access.GetInterface(ID3D11Texture2D);
        return new SharpDXTexture2D(pointer);
    }

    // Lets the OS's own capture picker (GraphicsCapturePicker) center itself
    // over this app's window - needs the app's HWND via IInitializeWithWindow,
    // which isn't exposed as a friendly projected API.
    public static void SetWindow(this GraphicsCapturePicker picker, IntPtr hwnd) =>
        picker.As<IInitializeWithWindow>().Initialize(hwnd);
}
