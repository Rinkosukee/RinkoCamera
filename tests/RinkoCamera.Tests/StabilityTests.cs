using System.Collections.Concurrent;
using System.Diagnostics;
using RinkoCamera.Core;
using RinkoCamera.Infrastructure;

namespace RinkoCamera.Tests;

public sealed class StabilityTests
{
    [Fact]
    public async Task StopBeforeFirstFrame_DoesNotCommitEmptyMp4()
    {
        var directory = TestArtifacts.CreateDirectory("cancel-before-first-frame");
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder();
        await using var controller = new RecordingController(new FakeCaptureSource(waitForFirstFrame: true), audio, encoder, new LocalFileStorage(), new NullLogger());
        await controller.StartAsync(TestOptions(directory), CancellationToken.None);
        await WaitForAsync(() => encoder.StartCalls > 0);
        var result = await controller.StopAsync(CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(RecordingState.Idle, controller.State);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task FinalizeFailure_IsErrorAndNeverCommitsCorruptMp4()
    {
        var directory = TestArtifacts.CreateDirectory("finalize-failure");
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder { ThrowOnFinalize = true };
        await using var controller = new RecordingController(new FakeCaptureSource(), audio, encoder, new LocalFileStorage(), new NullLogger());
        await controller.StartAsync(TestOptions(directory), CancellationToken.None);
        await WaitForAsync(() => encoder.FramesWritten > 0);
        await Assert.ThrowsAsync<RecordingException>(() => controller.StopAsync(CancellationToken.None));
        Assert.Equal(RecordingState.Error, controller.State);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Countdown_PublishesThreeTwoOneBeforeCaptureAndEncodingStart()
    {
        var directory = TestArtifacts.CreateDirectory("countdown");
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder();
        await using var controller = new RecordingController(new FakeCaptureSource(), audio, encoder, new LocalFileStorage(), new NullLogger());
        var ticks = new ConcurrentQueue<(int Seconds, long Time, RecordingState State, int EncoderStarts)>();
        controller.CountdownChanged += (_, seconds) => ticks.Enqueue((seconds, Stopwatch.GetTimestamp(), controller.State, encoder.StartCalls));

        await controller.StartAsync(TestOptions(directory) with { CountdownSeconds = 3 }, CancellationToken.None);
        for (var i = 0; i < 60 && encoder.FramesWritten == 0; i++) await Task.Delay(100);
        var result = await controller.StopAsync(CancellationToken.None);

        Assert.NotNull(result);
        var countdown = ticks.Where(tick => tick.Seconds > 0).ToArray();
        Assert.Equal(new[] { 3, 2, 1 }, countdown.Select(tick => tick.Seconds));
        Assert.All(countdown, tick =>
        {
            Assert.Equal(RecordingState.Preparing, tick.State);
            Assert.Equal(0, tick.EncoderStarts);
        });
        for (var i = 1; i < countdown.Length; i++)
            Assert.True(Stopwatch.GetElapsedTime(countdown[i - 1].Time, countdown[i].Time) >= TimeSpan.FromMilliseconds(900));
        var countdownEnd = ticks.First(tick => tick.Seconds == 0);
        Assert.True(Stopwatch.GetElapsedTime(countdown[0].Time, countdownEnd.Time) >= TimeSpan.FromMilliseconds(2900));
        Assert.True(encoder.FramesWritten > 0);
        Assert.Equal(0, ticks.Last().Seconds);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task StopDuringCountdown_ClearsDisplayAndCreatesNoVideo(int cancelAt)
    {
        var directory = TestArtifacts.CreateDirectory("cancel-countdown");
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder();
        await using var controller = new RecordingController(new FakeCaptureSource(), audio, encoder, new LocalFileStorage(), new NullLogger());
        var countdownReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = -1;
        controller.CountdownChanged += (_, seconds) =>
        {
            remaining = seconds;
            if (seconds == cancelAt) countdownReached.TrySetResult();
        };
        await controller.StartAsync(TestOptions(directory) with { CountdownSeconds = 3 }, CancellationToken.None);
        await countdownReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await controller.StopAsync(CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(RecordingState.Idle, controller.State);
        Assert.Equal(0, remaining);
        Assert.Equal(0, encoder.StartCalls);
        Assert.Equal(0, encoder.FramesWritten);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task RepeatedStartAndStop_CommitsExactlyOneFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RinkoCameraTests", Guid.NewGuid().ToString("N"));
        var capture = new FakeCaptureSource();
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder();
        await using var controller = new RecordingController(capture, audio, encoder, new LocalFileStorage(), new NullLogger());
        var options = TestOptions(directory);

        await controller.StartAsync(options, CancellationToken.None);
        await WaitForAsync(() => controller.State == RecordingState.Recording);
        var ignored = await controller.StartAsync(options, CancellationToken.None);
        Assert.Null(ignored);

        var result = await controller.StopAsync(CancellationToken.None);
        Assert.NotNull(result);
        var secondStop = await controller.StopAsync(CancellationToken.None);
        Assert.Equal(result.FilePath, secondStop?.FilePath);
        Assert.Single(Directory.GetFiles(directory, "*.mp4"));
    }

    [Fact]
    public async Task AudioDeviceFault_FinalizesAndKeepsPartialFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RinkoCameraTests", Guid.NewGuid().ToString("N"));
        var capture = new FakeCaptureSource();
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder();
        await using var controller = new RecordingController(capture, audio, encoder, new LocalFileStorage(), new NullLogger());
        var options = TestOptions(directory) with { AudioMode = AudioSourceMode.Microphone };

        await controller.StartAsync(options, CancellationToken.None);
        await WaitForAsync(() => controller.State == RecordingState.Recording);
        audio.RaiseFault("音频设备已断开或不可用");
        await WaitForAsync(() => controller.State == RecordingState.Error);

        var files = Directory.GetFiles(directory, "*.mp4");
        Assert.Single(files);
        Assert.True(new FileInfo(files[0]).Length > 0);
    }

    [Fact]
    public async Task TargetWindowClosed_FinalizesAndKeepsPartialFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RinkoCameraTests", Guid.NewGuid().ToString("N"));
        var capture = new FakeCaptureSource(throwAfterFirstFrame: true);
        await using var audio = new FakeAudioSource();
        await using var encoder = new FakeEncoder();
        await using var controller = new RecordingController(capture, audio, encoder, new LocalFileStorage(), new NullLogger());

        await controller.StartAsync(TestOptions(directory), CancellationToken.None);
        await WaitForAsync(() => controller.State == RecordingState.Error);

        var files = Directory.GetFiles(directory, "*.mp4");
        Assert.Single(files);
        Assert.True(new FileInfo(files[0]).Length > 0);
    }

    private static RecordingOptions TestOptions(string directory) => new()
    {
        OutputDirectory = directory,
        Target = new CaptureTarget(CaptureTargetKind.FullScreen, "test", "test"),
        AudioMode = AudioSourceMode.Muted,
        Width = 16,
        Height = 16,
        FrameRate = 24,
        CountdownSeconds = 0
    };

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.True(predicate(), "condition was not reached before timeout");
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeCaptureSource : ICaptureSource
    {
        private readonly bool _throwAfterFirstFrame;
        private readonly bool _waitForFirstFrame;
        public FakeCaptureSource(bool throwAfterFirstFrame = false, bool waitForFirstFrame = false)
        { _throwAfterFirstFrame = throwAfterFirstFrame; _waitForFirstFrame = waitForFirstFrame; }
        public CaptureBackendMode ActiveMode => CaptureBackendMode.WgcGpu;
        public event EventHandler<string>? ModeChanged { add { } remove { } }
        public Task<IReadOnlyList<CaptureTarget>> EnumerateTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CaptureTarget>>([]);

        public async IAsyncEnumerable<VideoFrame> CaptureAsync(CaptureTarget target, int width, int height, int frameRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_waitForFirstFrame) await Task.Delay(Timeout.Infinite, cancellationToken);
            var bytes = new byte[Math.Max(2, width) * Math.Max(2, height) * 4];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new VideoFrame(width, height, width * 4, bytes, 0);
                if (_throwAfterFirstFrame) throw new RecordingException("目标窗口已关闭。");
                await Task.Delay(5, cancellationToken);
            }
        }
    }

    private sealed class FakeAudioSource : IAudioSource
    {
        public event Action<ReadOnlyMemory<byte>>? PcmAvailable { add { } remove { } }
        public event Action<string>? Faulted;
        public IReadOnlyList<AudioDevice> EnumerateDevices(bool input) => [];
        public Task StartAsync(AudioSourceMode mode, string? microphoneId, string? systemDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void RaiseFault(string message) => Faulted?.Invoke(message);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEncoder : IVideoEncoder
    {
        private FileStream? _stream;
        public int StartCalls;
        public int FramesWritten;
        public bool ThrowOnFinalize;
        public Task StartAsync(string temporaryPath, RecordingOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartCalls);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            _stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            return Task.CompletedTask;
        }

        public ValueTask WriteFrameAsync(VideoFrame frame, CancellationToken cancellationToken)
        {
            _stream!.WriteByte(0x01);
            Interlocked.Increment(ref FramesWritten);
            return ValueTask.CompletedTask;
        }

        public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, long timestamp100Ns, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public Task FinalizeAsync(CancellationToken cancellationToken)
        {
            _stream?.Dispose(); _stream = null;
            return ThrowOnFinalize ? Task.FromException(new RecordingException("模拟编码器收尾失败")) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { _stream?.Dispose(); _stream = null; return ValueTask.CompletedTask; }
    }
}
