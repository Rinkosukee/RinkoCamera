namespace RinkoCamera.Core;

public interface IRecordingController
{
    RecordingState State { get; }
    TimeSpan Elapsed { get; }
    event EventHandler<RecordingState>? StateChanged;
    event EventHandler<TimeSpan>? DurationChanged;
    event EventHandler<string>? ErrorRaised;
    event EventHandler<string>? CaptureModeChanged;
    /// <summary>Seconds until capture starts; zero clears the countdown display.</summary>
    event EventHandler<int>? CountdownChanged;
    event EventHandler<double>? AudioLevelChanged;
    Task<RecordingResult?> StartAsync(RecordingOptions options, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task<RecordingResult?> StopAsync(CancellationToken cancellationToken);
}

public interface ICaptureSource
{
    CaptureBackendMode ActiveMode { get; }
    event EventHandler<string>? ModeChanged;
    Task<IReadOnlyList<CaptureTarget>> EnumerateTargetsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VideoFrame> CaptureAsync(CaptureTarget target, int width, int height, int frameRate, CancellationToken cancellationToken);
}

/// <summary>Optional capture counters exposed by the WGC backend.</summary>
public interface ICaptureMetrics
{
    long CapturedFrames { get; }
    long DroppedFrames { get; }
}

public interface IAudioSource : IAsyncDisposable
{
    IReadOnlyList<AudioDevice> EnumerateDevices(bool input);
    Task StartAsync(AudioSourceMode mode, string? microphoneId, string? systemDeviceId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    event Action<ReadOnlyMemory<byte>>? PcmAvailable;

    /// <summary>
    /// Raised when an active endpoint reports a device or permission failure.
    /// The message is suitable for showing to the user and is intentionally
    /// independent of the NAudio implementation.
    /// </summary>
    event Action<string>? Faulted;
}

public interface IVideoEncoder : IAsyncDisposable
{
    Task StartAsync(string temporaryPath, RecordingOptions options, CancellationToken cancellationToken);
    ValueTask WriteFrameAsync(VideoFrame frame, CancellationToken cancellationToken);
    ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, long timestamp100Ns, CancellationToken cancellationToken);
    Task FinalizeAsync(CancellationToken cancellationToken);
}

public interface IFileStorage
{
    string GetOutputPath(RecordingOptions options, DateTimeOffset now);
    string GetTemporaryPath(string finalPath);
    void ValidateOutputDirectory(string directory);
    void EnsureFreeSpace(string directory, long minimumBytes);
    Task<RecordingResult> CommitAsync(string temporaryPath, string finalPath, TimeSpan duration, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken);
}

public sealed record UserSettings
{
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RinkoCamera");
    public CaptureTargetKind TargetKind { get; init; } = CaptureTargetKind.FullScreen;
    public string? TargetDisplayName { get; init; }
    public AudioSourceMode AudioMode { get; init; } = AudioSourceMode.System;
    public string? MicrophoneId { get; init; }
    public string? SystemDeviceId { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRate { get; init; } = 30;
    public VideoQuality Quality { get; init; } = VideoQuality.Medium;
    public int CountdownSeconds { get; init; } = 3;
    public uint HotkeyModifiers { get; init; }
    public uint HotkeyVirtualKey { get; init; } = 0x7A; // F11
}

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Pressed;
    bool Register(uint modifiers, uint virtualKey);
    void Unregister();
}

public interface IAppLogger
{
    void Info(string message);
    void Error(string message, Exception? exception = null);
}
