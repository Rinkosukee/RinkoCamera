using System.Text.Json;
using RinkoCamera.Core;

namespace RinkoCamera.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly string? _legacyPath;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General) { WriteIndented = true };

    public JsonSettingsStore(string? path = null, string? legacyPath = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RinkoCamera", "settings.json");
        _legacyPath = legacyPath ?? (path is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRecorder", "settings.json")
            : null);
    }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Prefer the new profile. Read the previous product's profile only
            // when no RinkoCamera profile exists; never modify the old file.
            var readPath = File.Exists(_path) ? _path : _legacyPath;
            if (readPath is null || !File.Exists(readPath)) return new UserSettings();
            await using var stream = File.OpenRead(readPath);
            return await JsonSerializer.DeserializeAsync<UserSettings>(stream, Options, cancellationToken) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new RecordingException("无法确定设置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
