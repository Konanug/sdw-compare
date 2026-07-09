using Microsoft.Extensions.Logging;
using System.IO;

namespace SolidWorksPartMatcher.App;

/// <summary>
/// Writes log lines to a plain text file. On each launch the file is
/// capped at 2 MB — lines beyond the cap are discarded to prevent the
/// log from growing unbounded on repeated runs.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB

    private readonly string _path;
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public RollingFileLoggerProvider(string path)
    {
        _path = path;

        // Rotate: if the existing log is over the cap, replace it.
        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            File.Delete(path);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false)
        {
            AutoFlush = true
        };

        _writer.WriteLine($"=== Tytle 3D Model Comparator started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly StreamWriter _writer;
        private readonly object _lock;

        public FileLogger(string category, StreamWriter writer, object lockObj)
        {
            _category = category;
            _writer = writer;
            _lock = lockObj;
        }

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;

            var prefix = level switch
            {
                LogLevel.Warning => "WARN ",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT ",
                LogLevel.Information => "INFO ",
                LogLevel.Debug => "DEBUG",
                _ => "     "
            };

            // Shorten category to the last segment to keep lines readable.
            var cat = _category.Contains('.')
                ? _category[(_category.LastIndexOf('.') + 1)..]
                : _category;

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {prefix} [{cat}] {formatter(state, exception)}";
            if (exception != null) line += $"\r\n  {exception}";

            lock (_lock) { _writer.WriteLine(line); }
        }
    }
}
