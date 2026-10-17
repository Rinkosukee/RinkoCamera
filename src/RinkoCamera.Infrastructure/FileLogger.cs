using RinkoCamera.Core;
using System.IO;

namespace RinkoCamera.Infrastructure;

public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private StreamWriter? _writer;

    public FileLogger(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RinkoCamera", "logs", "recorder.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), System.Text.Encoding.UTF8) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            _writer?.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? string.Empty : " " + exception)}");
        }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}
