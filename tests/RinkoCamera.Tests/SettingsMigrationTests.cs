using RinkoCamera.Core;
using RinkoCamera.Infrastructure;

namespace RinkoCamera.Tests;

public sealed class SettingsMigrationTests
{
    [Fact]
    public async Task MissingNewProfile_ReadsLegacyAndSavesOnlyToNewPath()
    {
        var directory = TestArtifacts.CreateDirectory("settings-rename");
        var oldPath = Path.Combine(directory, "legacy.json");
        var newPath = Path.Combine(directory, "current.json");
        var legacy = new UserSettings { Width = 1280, Height = 720, OutputDirectory = directory };
        await new JsonSettingsStore(oldPath).SaveAsync(legacy, CancellationToken.None);
        var oldBytes = await File.ReadAllBytesAsync(oldPath);
        var store = new JsonSettingsStore(newPath, oldPath);
        Assert.Equal(1280, (await store.LoadAsync(CancellationToken.None)).Width);
        Assert.False(File.Exists(newPath));
        await store.SaveAsync(legacy with { Width = 1920 }, CancellationToken.None);
        Assert.Equal(1920, (await store.LoadAsync(CancellationToken.None)).Width);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath));
    }

    [Fact]
    public async Task ExistingNewProfile_TakesPrecedenceOverLegacy()
    {
        var directory = TestArtifacts.CreateDirectory("settings-rename");
        var oldPath = Path.Combine(directory, "legacy.json");
        var newPath = Path.Combine(directory, "current.json");
        await new JsonSettingsStore(oldPath).SaveAsync(new UserSettings { Width = 1280 }, CancellationToken.None);
        await new JsonSettingsStore(newPath).SaveAsync(new UserSettings { Width = 2560 }, CancellationToken.None);
        Assert.Equal(2560, (await new JsonSettingsStore(newPath, oldPath).LoadAsync(CancellationToken.None)).Width);
    }
}
