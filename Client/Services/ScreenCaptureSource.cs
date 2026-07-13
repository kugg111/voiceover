using System.Runtime.InteropServices;
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
    private readonly int? _maxWidth;
    private readonly int? _maxHeight;

    private SizeInt32 _nativeSize;
    private D3D11Texture2D? _stagingTexture;
    private byte[] _pixelBuffer = Array.Empty<byte>();
    private byte[] _rowScratch = Array.Empty<byte>();

    // Remembers the last window/monitor picked this session (in-memory only
    // - a GraphicsCaptureItem can't meaningfully survive a restart anyway,
    // the window/monitor it points at may no longer exist) so repeat shares
    // don't have to reopen the OS picker from scratch every time.
    public static GraphicsCaptureItem? LastPickedItem { get; private set; }

    // maxWidth/maxHeight cap the published resolution (e.g. 720p) - null
    // means native/uncapped. Downscaling happens during the row-copy itself
    // (skip unneeded rows, sample columns from the ones we do read) rather
    // than copying the full native frame and discarding most of it
    // afterward, so a resolution cap also cuts the CPU cost of capture
    // itself, not just the encoder's input size.
    public ScreenCaptureSource(GraphicsCaptureItem item, int? maxWidth = null, int? maxHeight = null)
    {
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;

        // BgraSupport is required for Direct3D11CaptureFramePool to accept
        // this device - without it, frame pool creation fails outright.
        _d3dDevice = new D3D11Device(D3D11DriverType.Hardware, D3D11DeviceCreationFlags.BgraSupport);
        _winrtDevice = Direct3D11Interop.CreateDirect3DDeviceFromSharpDXDevice(_d3dDevice);

        _nativeSize = item.Size;
        var (outWidth, outHeight) = ComputeOutputSize(_nativeSize.Width, _nativeSize.Height, _maxWidth, _maxHeight);
        Source = new VideoSource(outWidth, outHeight);

        _framePool = Direct3D11CaptureFramePool.Create(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();
    }

    private static (int Width, int Height) ComputeOutputSize(int nativeWidth, int nativeHeight, int? maxWidth, int? maxHeight)
    {
        if (maxWidth is null || maxHeight is null) return (nativeWidth, nativeHeight);
        if (nativeWidth <= maxWidth.Value && nativeHeight <= maxHeight.Value) return (nativeWidth, nativeHeight);

        var scale = Math.Min((double)maxWidth.Value / nativeWidth, (double)maxHeight.Value / nativeHeight);
        // Even dimensions - most video encoders require them for chroma subsampling.
        var width = Math.Max(2, (int)(nativeWidth * scale) & ~1);
        var height = Math.Max(2, (int)(nativeHeight * scale) & ~1);
        return (width, height);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        if (frame.ContentSize.Width != _nativeSize.Width || frame.ContentSize.Height != _nativeSize.Height)
        {
            _nativeSize = frame.ContentSize;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _nativeSize);
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

        var nativeWidth = _nativeSize.Width;
        var nativeHeight = _nativeSize.Height;
        var (outWidth, outHeight) = ComputeOutputSize(nativeWidth, nativeHeight, _maxWidth, _maxHeight);
        var outRowBytes = outWidth * 4;
        if (_pixelBuffer.Length != outRowBytes * outHeight)
            _pixelBuffer = new byte[outRowBytes * outHeight];

        var box = _d3dDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, D3D11MapMode.Read, D3D11MapFlags.None);
        try
        {
            var src = box.DataPointer;
            if (outWidth == nativeWidth && outHeight == nativeHeight)
            {
                for (var y = 0; y < nativeHeight; y++)
                    Marshal.Copy(src + y * box.RowPitch, _pixelBuffer, y * outRowBytes, outRowBytes);
            }
            else
            {
                // Nearest-neighbor downscale: only the sampled rows ever
                // cross the P/Invoke boundary (native-width each), then
                // columns are sampled from that single row in cheap managed
                // memory - skips reading/copying rows we'd discard anyway.
                var nativeRowBytes = nativeWidth * 4;
                if (_rowScratch.Length != nativeRowBytes)
                    _rowScratch = new byte[nativeRowBytes];

                for (var outY = 0; outY < outHeight; outY++)
                {
                    var srcY = outY * nativeHeight / outHeight;
                    Marshal.Copy(src + srcY * box.RowPitch, _rowScratch, 0, nativeRowBytes);

                    var destRowOffset = outY * outRowBytes;
                    for (var outX = 0; outX < outWidth; outX++)
                    {
                        var srcX = outX * nativeWidth / outWidth;
                        Buffer.BlockCopy(_rowScratch, srcX * 4, _pixelBuffer, destRowOffset + outX * 4, 4);
                    }
                }
            }
        }
        finally
        {
            _d3dDevice.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
        }

        var videoFrame = new VideoFrame(outWidth, outHeight, VideoBufferType.Bgra, _pixelBuffer, null);
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
        var item = await picker.PickSingleItemAsync();
        if (item is not null) LastPickedItem = item;
        return item;
    }
}
