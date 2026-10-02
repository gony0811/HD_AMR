using Microsoft.Extensions.Logging;

namespace HD.AMR.Desktop.Infrastructure;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Information)
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HD.AMR", "logs");
        _minLevel = minLevel;
        Directory.CreateDirectory(_dir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(LogLevel level, string category, string message)
    {
        if (level < _minLevel) return;
        var now = DateTime.Now;
        var date = now.ToString("yyyy-MM-dd");
        var line = $"[{now:HH:mm:ss.fff}] [{level,-11}] [{category}] {message}";

        lock (_lock)
        {
            if (date != _currentDate)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(
                    Path.Combine(_dir, $"app-{date}.log"), append: true) { AutoFlush = true };
                _currentDate = date;
            }
            _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += Environment.NewLine + exception;
            provider.Write(logLevel, category, msg);
        }
    }
}
