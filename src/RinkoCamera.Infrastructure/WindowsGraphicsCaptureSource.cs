using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RinkoCamera.Core;
using Windows.Graphics.Capture;

namespace RinkoCamera.Infrastructure;

/// <summary>
/// Desktop target catalog backed by Windows.Graphics.Capture. Windows 10 1903+
/// uses the shared D3D11 frame-pool backend for windows and monitors; the GDI
/// bridge is retained only as an explicit compatibility fallback when runtime
/// capability or target creation fails.
/// </summary>
public sealed class WindowsGraphicsCaptureSource : ICaptureSource, ICaptureMetrics
{
    private CaptureBackendMode _activeMode = CaptureBackendMode.CompatibilityGdi;
    private long _capturedFrames;
    private long _droppedFrames;
    private bool _modeAnnounced;

    public CaptureBackendMode ActiveMode => _activeMode;
    public long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public event EventHandler<string>? ModeChanged;

    public Task<IReadOnlyList<CaptureTarget>> EnumerateTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<CaptureTarget>
        {
            new(CaptureTargetKind.FullScreen, "virtual-screen", "全屏", 0)
        };
        EnumWindows((hwnd, _) =>
        {
            if (cancellationToken.IsCancellationRequested || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var builder = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(hwnd, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (GetWindowThreadProcessId(hwnd, out var process) != 0 && process != Environment.ProcessId)
                result.Add(new CaptureTarget(CaptureTargetKind.Window, hwnd.ToString(), $"窗口 · {title}", hwnd));
            return true;
        }, 0);
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                    result.Add(new CaptureTarget(CaptureTargetKind.Monitor, monitor.ToString(), $"显示器 {result.Count(t => t.Kind == CaptureTargetKind.Monitor) + 1}", monitor));
            }
            return true;
        }, 0);
        return Task.FromResult<IReadOnlyList<CaptureTarget>>(result);
    }

    public async IAsyncEnumerable<VideoFrame> CaptureAsync(CaptureTarget target, int width, int height, int frameRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _capturedFrames, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        _modeAnnounced = false;
        if (!GraphicsCaptureSession.IsSupported())
        {
            if (target.Kind == CaptureTargetKind.Window)
                throw new RecordingException("此系统的 Windows.Graphics.Capture 不可用，无法独立捕获指定窗口。请更新 Windows/显卡驱动，或选择显示器录制。");
            SetMode(CaptureBackendMode.CompatibilityGdi, "Windows.Graphics.Capture 不可用，使用兼容捕获路径。");
        }
        ValidateTarget(target);
        if (GraphicsCaptureSession.IsSupported())
        {
            var wgcFrames = new Direct3D11WgcCapture();
            var delivered = false;
            try
            {
                await foreach (var frame in wgcFrames.CaptureAsync(target, frameRate, cancellationToken).ConfigureAwait(false))
                {
                    delivered = true;
                    Interlocked.Increment(ref _capturedFrames);
                    SetMode(CaptureBackendMode.WgcGpu, "Windows.Graphics.Capture + Direct3D11 GPU 捕获");
                    yield return frame.Width == width && frame.Height == height ? frame : ResizeFrame(frame, width, height);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _droppedFrames, wgcFrames.DroppedFrames);
            }
            if (delivered || cancellationToken.IsCancellationRequested) yield break;
            if (target.Kind == CaptureTargetKind.Window)
                throw new RecordingException("指定窗口没有返回画面，录制已停止。请还原窗口或更换目标。");
            if (target.Kind == CaptureTargetKind.Window && IsIconic(target.Handle))
                throw new RecordingException("目标窗口已最小化，当前系统无法通过兼容路径捕获其内容。请还原窗口后重试。");
            SetMode(CaptureBackendMode.CompatibilityGdi, "WGC 无法创建目标，使用兼容捕获路径。");
        }
        var interval = TimeSpan.FromSeconds(1d / Math.Clamp(frameRate, 1, 60));
        var stopwatch = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (target.Kind == CaptureTargetKind.Window && IsIconic(target.Handle))
                throw new RecordingException("目标窗口已最小化，当前捕获路径无法保证画面内容。请还原窗口后停止录制。");
            var bounds = GetBounds(target);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new RecordingException("录制目标已消失或尺寸无效。");
            var frame = CaptureBitmap(bounds, width, height, stopwatch.Elapsed.Ticks);
            Interlocked.Increment(ref _capturedFrames);
            yield return frame;
            try { await Task.Delay(interval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private void SetMode(CaptureBackendMode mode, string message)
    {
        if (_modeAnnounced && _activeMode == mode) return;
        _activeMode = mode;
        _modeAnnounced = true;
        ModeChanged?.Invoke(this, message);
    }

    internal static void ValidateTarget(CaptureTarget target)
    {
        if (target.Kind != CaptureTargetKind.Window) return;
        if (!IsWindow(target.Handle)) throw new RecordingException("录制目标窗口已关闭。");
        if (IsIconic(target.Handle)) throw new RecordingException("录制目标窗口已最小化，无法保证画面更新。请还原窗口后重新录制。");
        if (GetWindowDisplayAffinity(target.Handle, out var affinity) && affinity != 0)
            throw new RecordingException("录制目标窗口已启用内容保护，无法录制此窗口。");
    }

    private static Rectangle GetBounds(CaptureTarget target)
    {
        if (target.Kind == CaptureTargetKind.FullScreen)
        {
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var right = GetSystemMetrics(78);
            var bottom = GetSystemMetrics(79);
            return new Rectangle(left, top, right, bottom);
        }
        if (target.Kind == CaptureTargetKind.Window)
        {
            if (!GetWindowRect(target.Handle, out var rect)) return Rectangle.Empty;
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        var monitor = target.Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom) : Rectangle.Empty;
    }

    private static VideoFrame CaptureBitmap(Rectangle source, int requestedWidth, int requestedHeight, long timestamp)
    {
        var width = Math.Max(2, requestedWidth > 0 ? requestedWidth : source.Width);
        var height = Math.Max(2, requestedHeight > 0 ? requestedHeight : source.Height);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var region = CaptureRegion(source);
            graphics.DrawImage(region, new Rectangle(0, 0, width, height));
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new VideoFrame(width, height, Math.Abs(data.Stride), bytes, timestamp);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static Bitmap CaptureRegion(Rectangle source)
    {
        var bitmap = new Bitmap(Math.Max(2, source.Width), Math.Max(2, source.Height), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(source.Left, source.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static VideoFrame ResizeFrame(VideoFrame source, int width, int height)
    {
        var targetWidth = Math.Max(2, width);
        var targetHeight = Math.Max(2, height);
        var handle = GCHandle.Alloc(source.Bgra32, GCHandleType.Pinned);
        try
        {
            using var input = new Bitmap(source.Width, source.Height, source.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var output = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(output)) graphics.DrawImage(input, new Rectangle(0, 0, targetWidth, targetHeight));
            var data = output.LockBits(new Rectangle(0, 0, targetWidth, targetHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[Math.Abs(data.Stride) * targetHeight];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return new VideoFrame(targetWidth, targetHeight, Math.Abs(data.Stride), bytes, source.Timestamp100Ns);
            }
            finally { output.UnlockBits(data); }
        }
        finally { handle.Free(); }
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
}
