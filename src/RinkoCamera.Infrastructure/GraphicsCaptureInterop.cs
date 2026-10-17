using System.Runtime.InteropServices;
using RinkoCamera.Core;
using Windows.Graphics.Capture;

namespace RinkoCamera.Infrastructure;

internal static class GraphicsCaptureInterop
{
    public static GraphicsCaptureItem CreateItem(CaptureTarget target)
    {
        var handle = target.Kind == CaptureTargetKind.FullScreen ? MonitorFromPoint(default, 1) : target.Handle;
        if (handle == 0) throw new RecordingException("录制目标句柄无效，请刷新并重新选择窗口。");
        var initialized = RoInitialize(1);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        nint name = 0, factory = 0, item = 0;
        try
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out name));
            var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref interopId, out factory));
            // HWND/HMONITOR are native handles, not Windows.UI.WindowId values.
            var slot = target.Kind == CaptureTargetKind.Window ? 3 : 4;
            var method = Marshal.GetDelegateForFunctionPointer<CreateItemDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), slot * IntPtr.Size));
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(method(factory, handle, ref itemId, out item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        catch (COMException ex)
        {
            throw new RecordingException($"Windows 无法捕获此目标（0x{ex.HResult:X8}）。请还原窗口并检查目标是否禁止屏幕捕获；受保护内容无法录制。", ex);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (name != 0) WindowsDeleteString(name);
            if (initialized >= 0) RoUninitialize();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateItemDelegate(nint self, nint handle, ref Guid iid, out nint item);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
}
