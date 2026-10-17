using System.Diagnostics;
using RinkoCamera.Core;

namespace RinkoCamera.Infrastructure;

public sealed class RecordingController : IRecordingController, IAsyncDisposable
{
    private readonly ICaptureSource _capture;
    private readonly IAudioSource _audio;
    private readonly IVideoEncoder _encoder;
    private readonly IFileStorage _storage;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private RecordingResult? _result;
    private Exception? _runError;
    private Stopwatch? _clock;
    private long _audioTimestamp;
    private long _capturedFrames;
    private long _encodedFrames;
    private long _audioSamples;
    private long _lastVideoTimestamp;
    private double _audioPeak;
    private long _lastMeterTimestamp;
    private AudioSourceMode _audioMode;
    private bool _stopRequested;
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private RecordingState _state = RecordingState.Idle;

    public RecordingController(ICaptureSource capture, IAudioSource audio, IVideoEncoder encoder, IFileStorage storage, IAppLogger logger)
    {
        _capture = capture;
        _audio = audio;
        _encoder = encoder;
        _storage = storage;
        _logger = logger;
        _audio.PcmAvailable += OnPcmAvailable;
        _capture.ModeChanged += OnCaptureModeChanged;
        _audio.Faulted += OnAudioFaulted;
    }

    public RecordingState State { get { lock (_gate) return _state; } }
    public TimeSpan Elapsed => _clock?.Elapsed ?? TimeSpan.Zero;
    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<TimeSpan>? DurationChanged;
    public event EventHandler<string>? ErrorRaised;
    public event EventHandler<string>? CaptureModeChanged;
    public event EventHandler<int>? CountdownChanged;
    public event EventHandler<double>? AudioLevelChanged;

    public Task<RecordingResult?> StartAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state is not (RecordingState.Idle or RecordingState.Error))
                return Task.FromResult<RecordingResult?>(null);
            SetStateLocked(RecordingState.Preparing);
            _result = null;
            _runError = null;
            _clock = null;
            _audioTimestamp = 0;
            _audioPeak = 0;
            _lastMeterTimestamp = 0;
            _audioMode = options.AudioMode;
            _stopRequested = false;
            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = Task.Run(() => RunAsync(options, _runCts.Token), CancellationToken.None);
        }
        return Task.FromResult<RecordingResult?>(null);
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state == RecordingState.Recording) { _clock?.Stop(); SetStateLocked(RecordingState.Paused); }
        }
        await Task.CompletedTask;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state == RecordingState.Paused) { _clock?.Start(); SetStateLocked(RecordingState.Recording); }
        }
        await Task.CompletedTask;
    }

    public async Task<RecordingResult?> StopAsync(CancellationToken cancellationToken)
    {
        Task? task;
        lock (_gate)
        {
            if (_state is RecordingState.Idle) return _result;
            if (_state is RecordingState.Recording or RecordingState.Paused or RecordingState.Preparing)
                SetStateLocked(RecordingState.Stopping);
            _stopRequested = true;
            _runCts?.Cancel();
            task = _runTask;
        }
        if (task is not null) await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_runError is not null) throw new RecordingException("录制失败。", _runError);
        return _result;
    }

    private async Task RunAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        string? finalPath = null;
        try
        {
            _storage.ValidateOutputDirectory(options.OutputDirectory);
            _storage.EnsureFreeSpace(options.OutputDirectory, 256L * 1024 * 1024);
            finalPath = _storage.GetOutputPath(options, DateTimeOffset.Now);
            temporaryPath = _storage.GetTemporaryPath(finalPath);
            _audioTimestamp = 0;
            Interlocked.Exchange(ref _capturedFrames, 0);
            Interlocked.Exchange(ref _encodedFrames, 0);
            Interlocked.Exchange(ref _audioSamples, 0);
            Interlocked.Exchange(ref _lastVideoTimestamp, 0);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            for (var remaining = Math.Clamp(options.CountdownSeconds, 0, 10); remaining > 0; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CountdownChanged?.Invoke(this, remaining);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            CountdownChanged?.Invoke(this, 0);
            cancellationToken.ThrowIfCancellationRequested();
            await _encoder.StartAsync(temporaryPath, options, cancellationToken).ConfigureAwait(false);
            _logger.Info($"音频模式：{options.AudioMode}；系统设备：{options.SystemDeviceId ?? "默认"}；麦克风：{options.MicrophoneId ?? "默认"}");
            var mediaStarted = false;
            await foreach (var frame in _capture.CaptureAsync(options.Target, options.Width, options.Height, options.FrameRate, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _capturedFrames);
                if (!mediaStarted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _clock = Stopwatch.StartNew();
                    await _audio.StartAsync(options.AudioMode, options.MicrophoneId, options.SystemDeviceId, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    SetState(RecordingState.Recording);
                    mediaStarted = true;
                }
                if (State != RecordingState.Recording) continue;
                // The active media clock excludes pauses and starts only when
                // the first video frame is ready, keeping audio and video aligned.
                var timestamp = _clock!.Elapsed.Ticks;
                var previousTimestamp = Interlocked.Read(ref _lastVideoTimestamp);
                if (timestamp <= previousTimestamp) timestamp = previousTimestamp + 1;
                Interlocked.Exchange(ref _lastVideoTimestamp, timestamp);
                await _encoder.WriteFrameAsync(frame with { Timestamp100Ns = timestamp }, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _encodedFrames);
                DurationChanged?.Invoke(this, _clock.Elapsed);
            }
            await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await FinalizeEncoderAsync(CancellationToken.None).ConfigureAwait(false);
            if (_stopRequested && Interlocked.Read(ref _encodedFrames) > 0 && temporaryPath is not null && finalPath is not null)
            {
                _result = AddMetrics(await _storage.CommitAsync(temporaryPath, finalPath, _clock?.Elapsed ?? TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false));
            }
            else if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            SetState(RecordingState.Idle);
        }
        catch (OperationCanceledException) when (_stopRequested)
        {
            try { await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            var finalized = true;
            try { await FinalizeEncoderAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                finalized = false;
                _runError = ex;
                _logger.Error("编码器结束失败", ex);
                try { await _encoder.DisposeAsync().ConfigureAwait(false); } catch (Exception disposeError) { _logger.Error("编码器释放失败", disposeError); }
            }
            if (finalized && Interlocked.Read(ref _encodedFrames) > 0 && temporaryPath is not null && finalPath is not null && File.Exists(temporaryPath))
            {
                try
                {
                    _result = AddMetrics(await _storage.CommitAsync(temporaryPath, finalPath, _clock?.Elapsed ?? TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false));
                    if (_runError is not null) ErrorRaised?.Invoke(this, $"{_runError.Message} 已保存已编码片段：{_result.FilePath}");
                }
                catch (Exception ex) { _runError = ex; }
            }
            else if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch (IOException ex) { _logger.Error("临时视频清理失败", ex); }
            }
            SetState(_runError is null ? RecordingState.Idle : RecordingState.Error);
        }
        catch (Exception ex)
        {
            _runError = ex;
            _logger.Error("录制发生错误", ex);
            ErrorRaised?.Invoke(this, ex is RecordingException ? ex.Message : $"录制失败：{ex.Message}");
            try { await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            // If a target window or endpoint disappears, retain the portion
            // already encoded by finalizing and atomically committing it.
            var preservePartial = _clock is not null && IsRecoverableSessionEnd(ex);
            try { await FinalizeEncoderAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception finalizeError)
            {
                _logger.Error("编码器结束失败", finalizeError);
                preservePartial = false;
                try { await _encoder.DisposeAsync().ConfigureAwait(false); } catch (Exception disposeError) { _logger.Error("编码器释放失败", disposeError); }
            }
            if (preservePartial && temporaryPath is { } partialTemporaryPath && finalPath is { } partialFinalPath && File.Exists(partialTemporaryPath))
            {
                try
                {
                    _result = AddMetrics(await _storage.CommitAsync(partialTemporaryPath, partialFinalPath, _clock?.Elapsed ?? TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false));
                    ErrorRaised?.Invoke(this, $"{ex.Message} 已保存已编码片段：{_result.FilePath}");
                }
                catch (Exception commitError) { _logger.Error("部分录制文件保存失败", commitError); _runError = commitError; }
            }
            else if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
            SetState(RecordingState.Error);
        }
        finally
        {
            CountdownChanged?.Invoke(this, 0);
        }
    }

    private void OnPcmAvailable(ReadOnlyMemory<byte> pcm)
    {
        if (State != RecordingState.Recording || _clock is null) return;
        _audioGate.Wait();
        try
        {
            if (State != RecordingState.Recording) return;
            try
            {
                var peak = PcmAudio.Peak(pcm.Span);
                _audioPeak = Math.Max(_audioPeak, peak);
                if (_clock.Elapsed.Ticks - _lastMeterTimestamp >= TimeSpan.TicksPerMillisecond * 100)
                {
                    _lastMeterTimestamp = _clock.Elapsed.Ticks;
                    AudioLevelChanged?.Invoke(this, peak);
                }
                // NAudio invokes this callback on an audio worker thread. A
                // synchronous wait keeps the callback serialized with MP4
                // finalization, preventing samples after the sink is closed.
                _encoder.WritePcmAsync(pcm, _audioTimestamp, CancellationToken.None).GetAwaiter().GetResult();
                _audioTimestamp += pcm.Length * TimeSpan.TicksPerSecond / (48000 * 2 * 2);
                Interlocked.Add(ref _audioSamples, pcm.Length / 4);
            }
            catch (Exception ex)
            {
                _logger.Error("音频帧写入失败", ex);
                OnAudioFaulted("音频编码失败，录制将安全停止。");
            }
        }
        finally { _audioGate.Release(); }
    }

    private async Task FinalizeEncoderAsync(CancellationToken cancellationToken)
    {
        _clock?.Stop();
        // Hold the same gate used by the audio callback across the async
        // finalize operation. This prevents a late WASAPI callback from
        // writing into a sink that has already been finalized.
        await _audioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _encoder.FinalizeAsync(cancellationToken).ConfigureAwait(false); }
        finally { _audioGate.Release(); }
    }

    private void OnAudioFaulted(string message)
    {
        var error = new RecordingException(message);
        lock (_gate)
        {
            if (_state is not (RecordingState.Preparing or RecordingState.Recording or RecordingState.Paused)) return;
            _runError ??= error;
            _stopRequested = true;
            SetStateLocked(RecordingState.Stopping);
            _runCts?.Cancel();
        }
        _logger.Error(message, error);
        ErrorRaised?.Invoke(this, message);
    }

    private static bool IsRecoverableSessionEnd(Exception exception)
    {
        if (exception is not RecordingException recordingException) return false;
        var message = recordingException.Message;
        return message.Contains("目标窗口", StringComparison.Ordinal)
            || message.Contains("录制目标", StringComparison.Ordinal)
            || message.Contains("音频设备", StringComparison.Ordinal);
    }

    private void OnCaptureModeChanged(object? sender, string message)
    {
        _logger.Info(message);
        CaptureModeChanged?.Invoke(this, message);
    }

    private RecordingResult AddMetrics(RecordingResult result)
    {
        var captureMetrics = _capture as ICaptureMetrics;
        var captured = captureMetrics?.CapturedFrames ?? Interlocked.Read(ref _capturedFrames);
        var dropped = captureMetrics?.DroppedFrames ?? 0;
        var metrics = new RecordingMetrics(
            captured,
            Interlocked.Read(ref _encodedFrames),
            dropped,
            Interlocked.Read(ref _audioSamples),
            result.Duration,
            _audioPeak);
        _logger.Info($"录制结束：{result.FilePath}，视频帧 {metrics.EncodedFrames}，音频样本 {metrics.AudioSamples}，峰值 {metrics.AudioPeak:F4}，时长 {metrics.Duration.TotalSeconds:F3}s");
        if (_audioMode != AudioSourceMode.Muted && _audioPeak < 0.0001)
            ErrorRaised?.Invoke(this, "视频已保存，但录制期间没有检测到声音。请检查所选播放设备/麦克风、设备静音和应用音量。");
        return result with { Metrics = metrics, CaptureMode = _capture.ActiveMode };
    }

    private void SetState(RecordingState next)
    {
        lock (_gate) SetStateLocked(next);
    }

    private void SetStateLocked(RecordingState next)
    {
        if (_state == next) return;
        RecordingStateMachine.EnsureTransition(_state, next);
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _audio.PcmAvailable -= OnPcmAvailable;
        _audio.Faulted -= OnAudioFaulted;
        _capture.ModeChanged -= OnCaptureModeChanged;
        await _audio.DisposeAsync().ConfigureAwait(false);
        await _encoder.DisposeAsync().ConfigureAwait(false);
        _runCts?.Dispose();
        _audioGate.Dispose();
    }
}
