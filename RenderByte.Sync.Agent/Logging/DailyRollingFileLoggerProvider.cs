namespace RenderByte.Sync.Agent.Logging;

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

public class DailyRollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly int _retainedDays;
    private StreamWriter? _currentWriter;
    private DateTime _currentDate;
    private readonly object _lock = new();

    public DailyRollingFileLoggerProvider(string logDirectory, int retainedDays = 14)
    {
        _logDirectory = logDirectory;
        _retainedDays = retainedDays;
        Directory.CreateDirectory(_logDirectory);
        CleanUpOldLogs();
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new DailyRollingFileLogger(categoryName, this);
    }

    internal void Log(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_currentDate.Date != now.Date || _currentWriter == null)
            {
                _currentWriter?.Dispose();
                _currentDate = now;
                var fileName = $"renderbyte-sync-{now:yyyy-MM-dd}.log";
                var path = Path.Combine(_logDirectory, fileName);
                _currentWriter = new StreamWriter(path, append: true) { AutoFlush = true };
                CleanUpOldLogs();
            }

            var logLine = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{categoryName}] {message}";
            if (exception != null)
            {
                logLine += Environment.NewLine + exception.ToString();
            }

            _currentWriter.WriteLine(logLine);
        }
    }

    private void CleanUpOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-_retainedDays);
            var files = Directory.GetFiles(_logDirectory, "renderbyte-sync-*.log");
            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var datePart = fileName.Replace("renderbyte-sync-", "");
                if (DateTime.TryParse(datePart, out var fileDate) && fileDate < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _currentWriter?.Dispose();
        }
    }
}

public class DailyRollingFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly DailyRollingFileLoggerProvider _provider;

    public DailyRollingFileLogger(string categoryName, DailyRollingFileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        _provider.Log(_categoryName, logLevel, formatter(state, exception), exception);
    }
}
