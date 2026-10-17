using System.Windows;
using System.Windows.Threading;

namespace RinkoCamera.Tests;

public sealed class WindowCloseTests
{
    [Fact]
    public Task CompletedCleanup_IsDeferredUntilOriginalClosingReturns() => OnDispatcherAsync(async () =>
    {
        var window = TestWindow();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        var calls = 0;
        var insideOriginalClose = false;
        var cleanupReentered = false;
        _ = new WindowCloseCoordinator(window, () =>
        {
            calls++;
            cleanupReentered = insideOriginalClose;
            return Task.CompletedTask;
        }, errors.Add);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        insideOriginalClose = true;
        window.Close();
        insideOriginalClose = false;
        Assert.Equal(0, calls);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cleanupReentered);
        Assert.Equal(1, calls);
        Assert.Empty(errors);
    });

    [Fact]
    public Task RepeatedClose_WaitsForCleanupOnceAndKeepsDispatcherResponsive() => OnDispatcherAsync(async () =>
    {
        var window = TestWindow();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        var calls = 0;
        _ = new WindowCloseCoordinator(window, () => { calls++; entered.TrySetResult(); return cleanup.Task; }, errors.Add);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show(); window.Close();
        await entered.Task;
        window.Close(); window.Close();
        Assert.True(window.IsVisible);
        Assert.False(closed.Task.IsCompleted);
        var heartbeat = false;
        await window.Dispatcher.InvokeAsync(() => heartbeat = true, DispatcherPriority.Background);
        Assert.True(heartbeat);
        Assert.Equal(1, calls);
        cleanup.SetResult();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(errors);
    });

    [Fact]
    public Task CleanupFailure_IsReportedAndCanBeRetriedWithoutUnhandledException() => OnDispatcherAsync(async () =>
    {
        var window = TestWindow();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        _ = new WindowCloseCoordinator(window, () => ++attempts == 1
            ? Task.FromException(new IOException("Simulated cleanup failure")) : Task.CompletedTask,
            error => reported.TrySetResult(error));
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show(); window.Close();
        Assert.IsType<IOException>(await reported.Task);
        Assert.True(window.IsVisible);
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
    });

    private static Window TestWindow() => new()
    {
        Title = "RinkoCamera close regression", Width = 240, Height = 100,
        ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20
    };

    private static Task OnDispatcherAsync(Func<Task> body)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
}
