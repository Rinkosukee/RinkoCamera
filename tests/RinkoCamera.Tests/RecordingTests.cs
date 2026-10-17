using RinkoCamera.Core;
using RinkoCamera.Infrastructure;

namespace RinkoCamera.Tests;

public sealed class RecordingTests
{
    [Fact]
    public void StateMachine_RejectsRecordingToIdle()
    {
        Assert.True(RecordingStateMachine.CanTransition(RecordingState.Idle, RecordingState.Preparing));
        Assert.False(RecordingStateMachine.CanTransition(RecordingState.Recording, RecordingState.Idle));
        Assert.Throws<InvalidOperationException>(() => RecordingStateMachine.EnsureTransition(RecordingState.Recording, RecordingState.Idle));
    }

    [Fact]
    public void FileStorage_GeneratesCollisionFreeNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RinkoCameraTests", Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorage();
        var options = new RecordingOptions { OutputDirectory = directory };
        var first = storage.GetOutputPath(options, new DateTimeOffset(2026, 10, 17, 10, 20, 30, TimeSpan.Zero));
        File.WriteAllBytes(first, []);
        var second = storage.GetOutputPath(options, new DateTimeOffset(2026, 10, 17, 10, 20, 30, TimeSpan.Zero));
        Assert.NotEqual(first, second);
        Assert.EndsWith("-1.mp4", second);
    }

    [Fact]
    public void FileStorage_RejectsBlankDirectory()
    {
        var storage = new LocalFileStorage();
        Assert.Throws<RecordingException>(() => storage.ValidateOutputDirectory("  "));
    }

    [Fact]
    public void FileStorage_TemporaryPathKeepsMp4Extension()
    {
        var storage = new LocalFileStorage();
        var temporary = storage.GetTemporaryPath("D:\\Recordings\\capture.mp4");
        Assert.EndsWith(".tmp.mp4", temporary);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "RinkoCameraTests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new UserSettings { OutputDirectory = "D:\\Recordings", FrameRate = 60, AudioMode = AudioSourceMode.System, SystemDeviceId = "test-render-endpoint", TargetDisplayName = "窗口 · Test app" };
        await store.SaveAsync(settings, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(settings.OutputDirectory, loaded.OutputDirectory);
        Assert.Equal(settings.FrameRate, loaded.FrameRate);
        Assert.Equal(settings.AudioMode, loaded.AudioMode);
        Assert.Equal(settings.SystemDeviceId, loaded.SystemDeviceId);
        Assert.Equal(settings.TargetDisplayName, loaded.TargetDisplayName);
    }

    [Fact]
    public async Task FileStorage_CommitMissingTemporaryFileFailsClearly()
    {
        var storage = new LocalFileStorage();
        var ex = await Assert.ThrowsAsync<RecordingException>(() => storage.CommitAsync("missing.tmp", "final.mp4", TimeSpan.Zero, CancellationToken.None));
        Assert.Contains("临时文件", ex.Message);
    }
}
