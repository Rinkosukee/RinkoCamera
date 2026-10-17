using System.Runtime.InteropServices;
using System.Diagnostics;
using RinkoCamera.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RinkoCamera.Infrastructure;

public sealed class NaudioCaptureSource : IAudioSource
{
    private readonly List<IWaveIn> _captures = [];
    private readonly Dictionary<IWaveIn, BufferedWaveProvider> _mixBuffers = [];
    private readonly object _mixGate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _deviceGate = new();
    private readonly List<MMDevice> _activeDevices = [];
    private Timer? _deviceMonitor;
    private bool _deviceFaultRaised;
    private bool _usesDefaultMicrophone;
    private bool _usesDefaultSystem;
    private AudioSourceMode _mode;
    private bool _stopping;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;

    public event Action<ReadOnlyMemory<byte>>? PcmAvailable;
    public event Action<string>? Faulted;

    public IReadOnlyList<AudioDevice> EnumerateDevices(bool input)
    {
        var flow = input ? DataFlow.Capture : DataFlow.Render;
        var devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        string? defaultId = null;
        try { using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = defaultDevice.ID; }
        catch (COMException) { }
        var result = new List<AudioDevice>();
        foreach (var device in devices)
        {
            using (device) result.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId, input));
        }
        return result;
    }

    public Task StartAsync(AudioSourceMode mode, string? microphoneId, string? systemDeviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCaptures();
        _mode = mode;
        _usesDefaultMicrophone = false;
        try
        {
            if (mode is AudioSourceMode.System or AudioSourceMode.SystemAndMicrophone)
            {
                _usesDefaultSystem = string.IsNullOrWhiteSpace(systemDeviceId);
                var device = _usesDefaultSystem ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : _enumerator.GetDevice(systemDeviceId!);
                TrackDevice(device);
                var loopback = new WasapiLoopbackCapture(device);
                loopback.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                loopback.DataAvailable += OnDataAvailable;
                loopback.RecordingStopped += OnRecordingStopped;
                _captures.Add(loopback);
                _mixBuffers[loopback] = CreateBuffer();
            }
            if (mode is AudioSourceMode.Microphone or AudioSourceMode.SystemAndMicrophone)
            {
                _usesDefaultMicrophone = string.IsNullOrWhiteSpace(microphoneId);
                var device = string.IsNullOrWhiteSpace(microphoneId)
                    ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : _enumerator.GetDevice(microphoneId);
                TrackDevice(device);
                var capture = new WasapiCapture(device);
                capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _captures.Add(capture);
                _mixBuffers[capture] = CreateBuffer();
            }
            _stopping = false;
            _deviceFaultRaised = false;
            foreach (var capture in _captures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                capture.StartRecording();
            }
            if (_captures.Count > 0)
            {
                _pumpCancellation = new CancellationTokenSource();
                _pumpTask = Task.Run(() => PumpAsync(_pumpCancellation.Token));
            }
            lock (_deviceGate)
            {
                if (_activeDevices.Count > 0)
                    _deviceMonitor = new Timer(_ => CheckDeviceStates(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
        catch (OperationCanceledException)
        {
            StopCaptures();
            throw;
        }
        catch (Exception ex) when (ex is NAudio.MmException or COMException or InvalidOperationException or ArgumentException)
        {
            StopCaptures();
            throw new RecordingException("音频设备不可用或没有访问权限，请检查系统声音设置。", ex);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stopping = true;
        _deviceMonitor?.Dispose();
        _deviceMonitor = null;
        _pumpCancellation?.Cancel();
        if (_pumpTask is not null) await _pumpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        _pumpTask = null;
        _pumpCancellation?.Dispose();
        _pumpCancellation = null;
        StopCaptures();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        if (sender is not IWaveIn source) return;
        // A WASAPI callback can race StopRecording/Dispose. Ignore callbacks
        // from an endpoint that is no longer part of the active session so a
        // late packet cannot leak into the next recording.
        lock (_mixGate)
        {
            if (!_mixBuffers.ContainsKey(source)) return;
        }
        try
        {
            var copy = PcmAudio.ConvertToStereo16(source.WaveFormat, e.Buffer.AsSpan(0, e.BytesRecorded));
            lock (_mixGate)
                if (_mixBuffers.TryGetValue(source, out var buffer)) buffer.AddSamples(copy, 0, copy.Length);
        }
        catch (Exception ex) { RaiseFault($"音频采集失败：{ex.Message}"); }
    }

    private static BufferedWaveProvider CreateBuffer() => new(PcmAudio.Format)
    {
        BufferDuration = TimeSpan.FromSeconds(2), ReadFully = true, DiscardOnBufferOverflow = false
    };

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var left = new byte[PcmAudio.ChunkBytes];
        var right = new byte[PcmAudio.ChunkBytes];
        var mixed = new byte[PcmAudio.ChunkBytes];
        var clock = Stopwatch.StartNew();
        long produced = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var due = (long)(clock.Elapsed.TotalSeconds * 48000);
                if (due - produced > 96000) throw new RecordingException("音频编码积压超过两秒，录制将安全停止。");
                while (produced + 480 <= due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_mixGate)
                    {
                        var buffers = _mixBuffers.Values.GetEnumerator();
                        if (!buffers.MoveNext()) return;
                        buffers.Current.Read(left, 0, left.Length);
                        if (buffers.MoveNext()) buffers.Current.Read(right, 0, right.Length);
                        else Array.Clear(right);
                    }
                    // A silent/inactive endpoint contributes zeros. It must
                    // never prevent the other endpoint from reaching the MP4.
                    PcmAudio.Mix(left, right, mixed);
                    PcmAvailable?.Invoke(mixed);
                    produced += 480;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { RaiseFault($"音频处理失败：{ex.Message}"); }
    }

    private void RaiseFault(string message)
    {
        if (_stopping || _deviceFaultRaised) return;
        _deviceFaultRaised = true;
        Faulted?.Invoke(message);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _stopping) return;
        var detail = e.Exception.Message;
        Faulted?.Invoke(string.IsNullOrWhiteSpace(detail)
            ? "音频设备已断开或不可用，录制将安全停止。"
            : $"音频设备已断开或不可用，录制将安全停止：{detail}");
    }

    private void TrackDevice(MMDevice device)
    {
        lock (_deviceGate) _activeDevices.Add(device);
    }

    private void StopCaptures()
    {
        _stopping = true;
        _pumpCancellation?.Cancel();
        _deviceMonitor?.Dispose();
        _deviceMonitor = null;
        foreach (var capture in _captures)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        _captures.Clear();
        lock (_mixGate) _mixBuffers.Clear();
        lock (_deviceGate)
        {
            foreach (var device in _activeDevices) device.Dispose();
            _activeDevices.Clear();
        }
    }

    private void CheckDeviceStates()
    {
        if (_stopping || _deviceFaultRaised) return;
        MMDevice[] devices;
        lock (_deviceGate) devices = _activeDevices.ToArray();
        if (_usesDefaultSystem && _mode is (AudioSourceMode.System or AudioSourceMode.SystemAndMicrophone))
        {
            try
            {
                using var currentDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (!devices.Any(device => device.ID == currentDefault.ID))
                {
                    _deviceFaultRaised = true;
                    Faulted?.Invoke("系统声音输出设备已切换，录制将安全停止，请重新选择设备后重试。");
                    return;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NAudio.MmException)
            {
                _deviceFaultRaised = true;
                Faulted?.Invoke("系统声音输出设备不可用，录制将安全停止。");
                return;
            }
        }
        if (_usesDefaultMicrophone && _mode is (AudioSourceMode.Microphone or AudioSourceMode.SystemAndMicrophone))
        {
            // A selected microphone is intentionally not replaced silently;
            // a missing or disabled endpoint is reported to the controller.
            // For the default microphone, detect a runtime default switch.
            try
            {
                using var currentDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (!devices.Any(device => device.ID == currentDefault.ID))
                {
                    _deviceFaultRaised = true;
                    Faulted?.Invoke("麦克风设备已切换，录制将安全停止，请重新选择设备后重试。");
                    return;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NAudio.MmException)
            {
                _deviceFaultRaised = true;
                Faulted?.Invoke("麦克风设备不可用，录制将安全停止。");
                return;
            }
        }
        foreach (var device in devices)
        {
            try
            {
                if (device.State == DeviceState.Active) continue;
                _deviceFaultRaised = true;
                Faulted?.Invoke($"音频设备“{device.FriendlyName}”已禁用或断开，录制将安全停止。");
                return;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _deviceFaultRaised = true;
                Faulted?.Invoke("音频设备已断开或不可用，录制将安全停止。");
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _enumerator.Dispose();
    }
}
