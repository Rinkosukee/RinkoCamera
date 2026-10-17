namespace RinkoCamera.Core;

public enum RecordingState { Idle, Preparing, Recording, Paused, Stopping, Error }
public enum CaptureTargetKind { Window, Monitor, FullScreen }
public enum AudioSourceMode { Muted, System, Microphone, SystemAndMicrophone }
public enum VideoQuality { Low, Medium, High }
public enum CaptureBackendMode { WgcGpu, CompatibilityGdi }

public sealed record CaptureTarget(CaptureTargetKind Kind, string Id, string DisplayName, nint Handle = 0);
public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool IsInput);
/// <summary>BGRA32 pixels in top-to-bottom row order, with a positive byte stride.</summary>
public sealed record VideoFrame(int Width, int Height, int Stride, byte[] Bgra32, long Timestamp100Ns);

public sealed record RecordingOptions
{
    public CaptureTarget Target { get; init; } = new(CaptureTargetKind.FullScreen, "virtual-screen", "全屏");
    public AudioSourceMode AudioMode { get; init; } = AudioSourceMode.Muted;
    public string? MicrophoneId { get; init; }
    public string? SystemDeviceId { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRate { get; init; } = 30;
    public VideoQuality Quality { get; init; } = VideoQuality.Medium;
    public string OutputDirectory { get; init; } = string.Empty;
    public int CountdownSeconds { get; init; } = 3;
}

public sealed record RecordingMetrics(long CapturedFrames, long EncodedFrames, long DroppedFrames, long AudioSamples, TimeSpan Duration, double AudioPeak = 0);
public sealed record RecordingResult(string FilePath, TimeSpan Duration, long BytesWritten, RecordingMetrics? Metrics = null, CaptureBackendMode CaptureMode = CaptureBackendMode.CompatibilityGdi);

public sealed class RecordingException(string message, Exception? inner = null) : Exception(message, inner);

public static class RecordingStateMachine
{
    private static readonly IReadOnlyDictionary<RecordingState, IReadOnlySet<RecordingState>> Transitions =
        new Dictionary<RecordingState, IReadOnlySet<RecordingState>>
        {
            [RecordingState.Idle] = new HashSet<RecordingState> { RecordingState.Preparing },
            [RecordingState.Preparing] = new HashSet<RecordingState> { RecordingState.Recording, RecordingState.Stopping, RecordingState.Idle, RecordingState.Error },
            [RecordingState.Recording] = new HashSet<RecordingState> { RecordingState.Paused, RecordingState.Stopping, RecordingState.Error },
            [RecordingState.Paused] = new HashSet<RecordingState> { RecordingState.Recording, RecordingState.Stopping, RecordingState.Error },
            [RecordingState.Stopping] = new HashSet<RecordingState> { RecordingState.Idle, RecordingState.Error },
            [RecordingState.Error] = new HashSet<RecordingState> { RecordingState.Idle, RecordingState.Preparing }
        };

    public static bool CanTransition(RecordingState from, RecordingState to) =>
        from == to || Transitions.TryGetValue(from, out var states) && states.Contains(to);

    public static void EnsureTransition(RecordingState from, RecordingState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"不允许从 {from} 转换到 {to}。");
    }
}

public static class RecordingClock
{
    public static long To100Nanoseconds(TimeSpan value) => checked(value.Ticks);
    public static TimeSpan From100Nanoseconds(long value) => TimeSpan.FromTicks(value);
}
