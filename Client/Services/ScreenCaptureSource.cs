using LiveKit.Proto;
using LiveKit.Rtc;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11DeviceCreationFlags = SharpDX.Direct3D11.DeviceCreationFlags;
using D3D11DriverType = SharpDX.Direct3D.DriverType;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using D3D11MapMode = SharpDX.Direct3D11.MapMode;
using D3D11ResourceOptionFlags = SharpDX.Direct3D11.ResourceOptionFlags;
using D3D11ResourceUsage = SharpDX.Direct3D11.ResourceUsage;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;

namespace Voiceover.Client.Services;

// Captures a monitor or window (whatever GraphicsCaptureItem the caller
// picked via the OS's own GraphicsCapturePicker) and feeds frames to a
// LiveKit VideoSource for publishing - the screen-share analog of
// MicCaptureSource. Frames are captured as BGRA8 (DXGI B8G8R8A8_UNorm) and
// published as-is: the earlier throughput spike (see the plan) confirmed
// LiveKit's client accepts VideoBufferType.Bgra directly, so there's no
// custom I420/YUV conversion needed here - the Rust FFI core handles
// whatever conversion the encoder actually needs internally.
public class ScreenCaptureSource : IDisposable
{
    public VideoSource Source { get; }

    private readonly D3D11Device _d3dDevice;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private SizeInt32 _lastSize;
    private D3D11Texture2D? _stagingTexture;
    private byte[] _pixelBuffer = Array.Empty<byte>();

    public ScreenCaptureSource(GraphicsCaptureItem item)
    {
        // BgraSupport is required for Direct3D11CaptureFramePool to accept
        // this device - without it, frame pool creation fails outright.
        _d3dDevice = new D3D11Device(D3D11DriverType.Hardware, D3D11DeviceCreationFlags.BgraSupport);
        _winrtDevice = Direct3D11Interop.CreateDirect3DDeviceFromSharpDXDevice(_d3dDevice);

        Source = new VideoSource(item.Size.Width, item.Size.Height);
        _lastSize = item.Size;

        _framePool = Direct3D11CaptureFramePool.Create(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        if (frame.ContentSize.Width != _lastSize.Width || frame.ContentSize.Height != _lastSize.Height)
        {
            _lastSize = frame.ContentSize;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _lastSize);
        }

        using var sourceTexture = Direct3D11Interop.CreateSharpDXTexture2D(frame.Surface);

        if (_stagingTexture is null)
        {
            var desc = sourceTexture.Description;
            desc.Usage = D3D11ResourceUsage.Staging;
            desc.CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read;
            desc.BindFlags = SharpDX.Direct3D11.BindFlags.None;
            desc.OptionFlags = D3D11ResourceOptionFlags.None;
            _stagingTexture = new D3D11Texture2D(_d3dDevice, desc);
        }

        _d3dDevice.ImmediateContext.CopyResource(sourceTexture, _stagingTexture);

        var width = _lastSize.Width;
        var height = _lastSize.Height;
        var rowBytes = width * 4;
        if (_pixelBuffer.Length != rowBytes * height)
            _pixelBuffer = new byte[rowBytes * height];

        var box = _d3dDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, D3D11MapMode.Read, D3D11MapFlags.None);
        try
        {
            var src = box.DataPointer;
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(src + y * box.RowPitch, _pixelBuffer, y * rowBytes, rowBytes);
        }
        finally
        {
            _d3dDevice.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
        }

        var videoFrame = new VideoFrame(width, height, VideoBufferType.Bgra, _pixelBuffer, null);
        Source.CaptureFrame(videoFrame, DateTimeOffset.UtcNow.Ticks / 10, VideoRotation._0);
    }

    public void Dispose()
    {
        _session.Dispose();
        _framePool.FrameArrived -= OnFrameArrived;
        _framePool.Dispose();
        _stagingTexture?.Dispose();
        _d3dDevice.Dispose();
        Source.Dispose();
    }

    // Opens the OS's own capture picker (lets the user choose a monitor or
    // window, with the standard system-drawn yellow capture border/privacy
    // indicator) - needs the app's own HWND for IInitializeWithWindow.
    public static async Task<GraphicsCaptureItem?> PickItemAsync(IntPtr ownerHwnd)
    {
        var picker = new GraphicsCapturePicker();
        picker.SetWindow(ownerHwnd);
        return await picker.PickSingleItemAsync();
    }
}
