using RinkoCamera.Core;

namespace RinkoCamera.Infrastructure;

public sealed class LocalFileStorage : IFileStorage
{
    private const long MinimumFreeBytes = 256L * 1024 * 1024;

    public string GetOutputPath(RecordingOptions options, DateTimeOffset now)
    {
        ValidateOutputDirectory(options.OutputDirectory);
        var stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var baseName = $"Recording-{stamp}";
        var candidate = Path.Combine(options.OutputDirectory, baseName + ".mp4");
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(options.OutputDirectory, $"{baseName}-{index++}.mp4");
        }
        return candidate;
    }

    // Keep the .mp4 suffix so Media Foundation can select its MP4 sink;
    // the temporary marker is inserted before the extension.
    public string GetTemporaryPath(string finalPath) => Path.Combine(Path.GetDirectoryName(finalPath)!, Path.GetFileNameWithoutExtension(finalPath) + ".tmp.mp4");

    public void ValidateOutputDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new RecordingException("保存目录不能为空。");
        try
        {
            var full = Path.GetFullPath(directory);
            Directory.CreateDirectory(full);
            if (!Directory.Exists(full))
                throw new RecordingException("无法创建保存目录。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw new RecordingException("保存目录无效或不可写。", ex);
        }
    }

    public void EnsureFreeSpace(string directory, long minimumBytes = MinimumFreeBytes)
    {
        ValidateOutputDirectory(directory);
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root))
            throw new RecordingException("无法确定保存磁盘。");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.AvailableFreeSpace < minimumBytes)
            throw new RecordingException($"保存磁盘可用空间不足，至少需要 {minimumBytes / (1024 * 1024)} MB。");
    }

    public async Task<RecordingResult> CommitAsync(string temporaryPath, string finalPath, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!File.Exists(temporaryPath))
            throw new RecordingException("录制临时文件不存在，无法完成保存。");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            _ = stream.Length;
            await stream.FlushAsync(cancellationToken);
            stream.Close();
            File.Move(temporaryPath, finalPath, overwrite: false);
            var length = new FileInfo(finalPath).Length;
            return new RecordingResult(finalPath, duration, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RecordingException("录制文件保存失败。", ex);
        }
    }
}
