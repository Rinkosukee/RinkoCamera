using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using RinkoCamera.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace RinkoCamera.Infrastructure;

internal sealed class Direct3D11WgcCapture
{
    private long _droppedFrames;
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public async IAsyncEnumerable<VideoFrame> CaptureAsync(CaptureTarget target, int frameRate,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var item = GraphicsCaptureInterop.CreateItem(target);
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0]);
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var deviceAbi));
        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(deviceAbi);
        Marshal.Release(deviceAbi);
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0) throw new RecordingException("录制目标尺寸无效，请先还原窗口。");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
        using var session = pool.CreateCaptureSession(item);
        using var context = device.ImmediateContext;
        ID3D11Texture2D? staging = null;
        var channel = Channel.CreateBounded<Direct3D11CaptureFrame>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true
        });
        TypedEventHandler<Direct3D11CaptureFramePool, object> arrived = (_, _) =>
        {
            Direct3D11CaptureFrame? frame = null;
            try
            {
                frame = pool.TryGetNextFrame();
                if (frame is null) return;
                if (!channel.Writer.TryWrite(frame))
                {
                    if (channel.Reader.TryRead(out var stale)) { stale.Dispose(); Interlocked.Increment(ref _droppedFrames); }
                    if (!channel.Writer.TryWrite(frame)) { frame.Dispose(); Interlocked.Increment(ref _droppedFrames); }
                }
            }
            catch (Exception error) { frame?.Dispose(); channel.Writer.TryComplete(error); }
        };
        TypedEventHandler<GraphicsCaptureItem, object> closed = (_, _) =>
            channel.Writer.TryComplete(new RecordingException("录制目标窗口已关闭。"));
        pool.FrameArrived += arrived;
        item.Closed += closed;
        var started = Stopwatch.StartNew();
        var delivered = false;
        VideoFrame? lastFrame = null;
        var sinceLastFrame = new Stopwatch();
        long previous = long.MinValue;
        var interval = TimeSpan.TicksPerSecond / Math.Clamp(frameRate, 1, 60);
        Task<bool>? waitForFrame = null;
        try
        {
            session.StartCapture();
            while (true)
            {
                WindowsGraphicsCaptureSource.ValidateTarget(target);
                bool ready;
                var timedOut = false;
                try
                {
                    waitForFrame ??= channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    ready = await waitForFrame.WaitAsync(TimeSpan.FromTicks(interval), cancellationToken).ConfigureAwait(false);
                    waitForFrame = null;
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    ready = false;
                }
                if (timedOut)
                {
                    WindowsGraphicsCaptureSource.ValidateTarget(target);
                    if (!delivered && started.Elapsed > TimeSpan.FromSeconds(6))
                        throw new RecordingException("录制目标没有返回画面；窗口可能不支持捕获或内容受保护。请还原窗口或更换目标。");
                    // WGC is change driven. A static window still needs timed
                    // samples so its MP4 duration matches the recording clock.
                    if (lastFrame is not null)
                        yield return lastFrame with { Timestamp100Ns = lastFrame.Timestamp100Ns + sinceLastFrame.Elapsed.Ticks };
                    continue;
                }
                if (!ready) break;
                if (!channel.Reader.TryRead(out var frame)) continue;
                var contentSize = frame.ContentSize;
                using (frame)
                {
                    var timestamp = frame.SystemRelativeTime.Ticks;
                    if (previous != long.MinValue && timestamp - previous < interval * 9 / 10)
                    { Interlocked.Increment(ref _droppedFrames); continue; }
                    previous = timestamp;
                    using var texture = GetTexture(frame.Surface);
                    var desc = texture.Description;
                    var width = Math.Min(contentSize.Width, (int)desc.Width);
                    var height = Math.Min(contentSize.Height, (int)desc.Height);
                    if (width <= 0 || height <= 0) throw new RecordingException("录制目标没有可捕获的画面。");
                    if (staging is null || staging.Description.Width != desc.Width || staging.Description.Height != desc.Height)
                    {
                        staging?.Dispose();
                        var stagingDesc = new Texture2DDescription(desc.Format, desc.Width, desc.Height, 1, 1,
                            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None);
                        staging = device.CreateTexture2D(in stagingDesc);
                    }
                    context.CopyResource(staging, texture);
                    var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    var stride = checked(width * 4);
                    var pixels = new byte[checked(stride * height)];
                    try
                    {
                        for (var row = 0; row < height; row++)
                            Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, pixels, row * stride, stride);
                    }
                    finally { context.Unmap(staging, 0); }
                    delivered = true;
                    lastFrame = new VideoFrame(width, height, stride, pixels, timestamp);
                    sinceLastFrame.Restart();
                    yield return lastFrame;
                }
                if (contentSize.Width > 0 && contentSize.Height > 0 &&
                    (contentSize.Width != size.Width || contentSize.Height != size.Height))
                {
                    size = contentSize;
                    pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                }
            }
        }
        finally
        {
            pool.FrameArrived -= arrived;
            item.Closed -= closed;
            session.Dispose();
            channel.Writer.TryComplete();
            while (channel.Reader.TryRead(out var pending)) pending.Dispose();
            staging?.Dispose();
        }
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        // GetIUnknownForObject creates a .NET CCW. Use the native WinRT ABI.
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0;
        try
        {
            var accessId = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, ref accessId, out access));
            var method = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
            var textureId = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(method(access, ref textureId, out var texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            if (access != 0) Marshal.Release(access);
            MarshalInterface<IDirect3DSurface>.DisposeAbi(abi);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(nint self, ref Guid iid, out nint result);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
