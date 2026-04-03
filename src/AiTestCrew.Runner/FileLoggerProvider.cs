using Microsoft.Extensions.Logging;

namespace AiTestCrew.Runner;

/// <summary>
/// Writes all log messages to a timestamped file so the console stays clean
/// while every debug detail is still captured for diagnosis.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _writer.WriteLine($"=== AI Test Crew — Log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(_writer, _lock, categoryName);

    public void Dispose()
    {
        _writer.WriteLine($"=== Log ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        _writer.Dispose();
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly StreamWriter _writer;
    private readonly object _lck;
    private readonly string _shortName;

    public FileLogger(StreamWriter writer, object lck, string category)
    {
        _writer    = writer;
        _lck       = lck;
        // Shorten long category names: "AiTestCrew.Agents.ApiAgent.ApiTestAgent" → "ApiTestAgent"
        _shortName = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var level = logLevel switch
        {
            LogLevel.Trace       => "TRC",
            LogLevel.Debug       => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning     => "WRN",
            LogLevel.Error       => "ERR",
            LogLevel.Critical    => "CRT",
            _                    => "???"
        };

        lock (_lck)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{_shortName,-30}] {message}");
            if (exception is not null)
                _writer.WriteLine(exception.ToString());
        }
    }
}
