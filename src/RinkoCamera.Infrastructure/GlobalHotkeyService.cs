using System.Runtime.InteropServices;
using System.Windows.Interop;
using RinkoCamera.Core;

namespace RinkoCamera.Infrastructure;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly HwndSource _source;
    private readonly int _id = Random.Shared.Next(1000, int.MaxValue);
    private bool _registered;

    public GlobalHotkeyService(nint hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("无法连接窗口句柄。");
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        _registered = RegisterHotKey(_source.Handle, _id, modifiers, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            _ = UnregisterHotKey(_source.Handle, _id);
            _registered = false;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hWnd, int id);
}
