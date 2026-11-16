using System;
using System.IO;
using Microsoft.Extensions.Logging;

public class FileLoggerProvider : ILoggerProvider {
    private readonly string _directory;
    private readonly object _sync = new();

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    public FileLoggerProvider(string directory) {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose() { }

    internal void Write(string line) {
        lock (_sync) {
            var file = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }
}

public class FileLogger : ILogger {
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider) {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (!IsEnabled(logLevel)) {
            return;
        }
        var message = formatter(state, exception);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category} - {message}";
        if (exception != null) {
            line += Environment.NewLine + exception;
        }
        _provider.Write(line);
    }
}