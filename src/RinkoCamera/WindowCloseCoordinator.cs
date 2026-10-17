using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace RinkoCamera;

/// <summary>Defers cleanup until WPF has returned from its first Closing event.</summary>
public sealed class WindowCloseCoordinator
{
    private readonly Window _window;
    private readonly Func<Task> _cleanup;
    private readonly Action<Exception> _reportError;
    private bool _allowClose;
    private bool _closed;
    private Task? _closeTask;

    public WindowCloseCoordinator(Window window, Func<Task> cleanup, Action<Exception> reportError)
    {
        _window = window;
        _cleanup = cleanup;
        _reportError = reportError;
        window.Closing += OnClosing;
        window.Closed += OnClosed;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        // Further X/Alt+F4 requests must not bypass pending MP4 finalization.
        if (_closeTask is not null) return;
        // Even a synchronously completed cleanup must not call Close() inside
        // the current Closing stack. Post a separate dispatcher operation.
        _closeTask = _window.Dispatcher.InvokeAsync(FinishCloseAsync, DispatcherPriority.Normal).Task.Unwrap();
    }

    private async Task FinishCloseAsync()
    {
        try
        {
            await _cleanup();
            if (_closed || _window.Dispatcher.HasShutdownStarted) return;
            _allowClose = true;
            _window.Close();
        }
        catch (Exception error)
        {
            _allowClose = false;
            _closeTask = null;
            _reportError(error);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
    }
}
