using System.Drawing;
using System.Runtime.InteropServices;
using RinkoCamera.Core;
using RinkoCamera.Infrastructure;
using Xunit.Abstractions;

namespace RinkoCamera.Tests;

[Collection("Desktop capture")]
public sealed class WindowCaptureIntegrationTests(ITestOutputHelper output)
{
    [DesktopFact]
    public async Task MinimizedOrProtectedWindow_FailsWithoutCapturingBlackFrames()
    {
        using var window = new CaptureFixtureWindow();
        var target = new CaptureTarget(CaptureTargetKind.Window, "fixture", "Fixture", window.Handle);
        window.Minimize();
        await using (var frames = new WindowsGraphicsCaptureSource().CaptureAsync(target, 400, 300, 30, CancellationToken.None).GetAsyncEnumerator())
        {
            var error = await Assert.ThrowsAsync<RecordingException>(async () => { await frames.MoveNextAsync(); });
            Assert.Contains("最小化", error.Message);
        }
        window.Restore();
        Assert.True(SetWindowDisplayAffinity(window.Handle, 0x11));
        await using (var frames = new WindowsGraphicsCaptureSource().CaptureAsync(target, 400, 300, 30, CancellationToken.None).GetAsyncEnumerator())
        {
            var error = await Assert.ThrowsAsync<RecordingException>(async () => { await frames.MoveNextAsync(); });
            Assert.Contains("保护", error.Message);
        }
        Assert.True(SetWindowDisplayAffinity(window.Handle, 0));
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DesktopFact]
    public async Task SpecifiedWindow_CapturesItsContentWhenCoveredAndResized()
    {
        using var window = new CaptureFixtureWindow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = new WindowsGraphicsCaptureSource();
        source.ModeChanged += (_, mode) => output.WriteLine(mode);
        var target = new CaptureTarget(CaptureTargetKind.Window, "fixture", "Capture test fixture", window.Handle);
        await using var frames = source.CaptureAsync(target, 400, 300, 30, timeout.Token).GetAsyncEnumerator();
        await ReadPatternAsync(frames);
        Assert.Equal(CaptureBackendMode.WgcGpu, source.ActiveMode);
        window.CoverAndResize();
        for (var i = 0; i < 5; i++) await ReadPatternAsync(frames);
        output.WriteLine("Captured asymmetric window pattern through occlusion and resize using WGC.");
        window.CloseTarget();
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            for (var i = 0; i < 10; i++) if (!await frames.MoveNextAsync()) throw new Exception("Target stream ended");
        });
        output.WriteLine($"Window closure reported: {error.Message}");
        Assert.Contains("关闭", error.ToString());
    }

    [DesktopFact]
    public async Task PrimaryMonitor_UsesWgcAndReturnsPixels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new WindowsGraphicsCaptureSource();
        var modeEvents = 0;
        source.ModeChanged += (_, _) => modeEvents++;
        for (var run = 0; run < 2; run++)
        {
            await foreach (var frame in source.CaptureAsync(new(CaptureTargetKind.FullScreen, "primary", "Primary display"), 640, 360, 30, timeout.Token))
            {
                Assert.Equal(CaptureBackendMode.WgcGpu, source.ActiveMode);
                Assert.Contains(frame.Bgra32, value => value > 0);
                output.WriteLine($"WGC monitor run {run + 1}: {frame.Width}x{frame.Height}");
                break;
            }
        }
        Assert.Equal(2, modeEvents);
    }

    private static async Task ReadPatternAsync(IAsyncEnumerator<VideoFrame> frames)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.True(await frames.MoveNextAsync());
            var frame = frames.Current;
            int Offset(int x, int y) => y * frame.Stride + x * 4;
            var red = Offset(frame.Width / 4, frame.Height / 3);
            var green = Offset(frame.Width * 3 / 4, frame.Height / 3);
            var blue = Offset(frame.Width / 4, frame.Height * 3 / 4);
            if (frame.Bgra32[red + 2] > 200 && frame.Bgra32[red] < 30 &&
                frame.Bgra32[green + 1] > 100 && frame.Bgra32[green + 2] < 30 &&
                frame.Bgra32[blue] > 200 && frame.Bgra32[blue + 2] < 30) return;
        }
        Assert.Fail("WGC did not capture the target window's red/green/blue pattern.");
    }

    private sealed class CaptureFixtureWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly Form _form;
        private Form? _cover;
        public nint Handle { get; }
        public CaptureFixtureWindow()
        {
            var ready = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                var form = new Form { Text = "RinkoCamera WGC regression fixture", ClientSize = new Size(400, 300), Location = new Point(100, 100), StartPosition = FormStartPosition.Manual };
                form.Paint += (_, args) =>
                {
                    var w = form.ClientSize.Width / 2; var h = form.ClientSize.Height / 2;
                    args.Graphics.FillRectangle(Brushes.Red, 0, 0, w, h);
                    args.Graphics.FillRectangle(Brushes.Lime, w, 0, w, h);
                    args.Graphics.FillRectangle(Brushes.Blue, 0, h, w, h);
                    args.Graphics.FillRectangle(Brushes.White, w, h, w, h);
                };
                form.Resize += (_, _) => form.Invalidate();
                form.Shown += (_, _) => ready.SetResult(form);
                System.Windows.Forms.Application.Run(form);
                form.Dispose();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _form = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Handle = (nint)_form.Invoke(() => _form.Handle);
        }
        public void CoverAndResize() => _form.Invoke(() =>
        {
            _form.ClientSize = new Size(500, 340);
            _form.Location = new Point(150, 150);
            _cover = new Form { Text = "Occluder", BackColor = Color.Black, Size = new Size(700, 600), Location = new Point(90, 90), StartPosition = FormStartPosition.Manual };
            _cover.Show(); _cover.BringToFront();
        });
        public void CloseTarget() => _form.Invoke(() => { _cover?.Close(); _form.Close(); });
        public void Minimize() => _form.Invoke(() => _form.WindowState = FormWindowState.Minimized);
        public void Restore() => _form.Invoke(() => _form.WindowState = FormWindowState.Normal);
        public void Dispose()
        {
            if (!_form.IsDisposed) CloseTarget();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOCALRECORDER_DESKTOP_TESTS") != "1")
            Skip = "Set LOCALRECORDER_DESKTOP_TESTS=1 in an interactive Windows desktop to run hardware capture tests.";
    }
}

[CollectionDefinition("Desktop capture", DisableParallelization = true)]
public sealed class DesktopCaptureCollection;
