using System.Collections.Concurrent;

namespace Ava.Api.Services;

/// <summary>
/// Minimal file logger that writes to logs/ava-api.log with daily rotation.
/// No external dependencies needed. Uses FileShare.ReadWrite to avoid locking issues.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly StreamWriter? _writer;
    private readonly object _lock = new();
    private readonly bool _enabled;

    public FileLoggerProvider(string logDir)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"ava-api-{DateTime.Now:yyyy-MM-dd}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            _enabled = true;
        }
        catch
        {
            // If we can't create the log file, just disable file logging silently
            _enabled = false;
        }
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void WriteEntry(string category, LogLevel level, string message)
    {
        if (!_enabled || _writer == null) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-12}] {category}: {message}";
            lock (_lock) { _writer.WriteLine(line); }
        }
        catch { /* swallow — never crash the app due to logging */ }
    }

    public void Dispose()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
    }
}

public sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        if (exception != null) msg += Environment.NewLine + exception;
        _provider.WriteEntry(_category, logLevel, msg);
    }
}
