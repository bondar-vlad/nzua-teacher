using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NzuaTeacher.Services;

/// <summary>Пише помилки застосунку у %APPDATA%\nzua-teacher\logs\, щоб їх можна було переглянути після збою.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private readonly LogLevel _minLevel;

    static FileLoggerProvider()
    {
        var thread = new Thread(WriteLoop) { IsBackground = true, Name = "nzua-file-log" };
        thread.Start();
    }

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Warning) => _minLevel = minLevel;

    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(NzuaTeacher.Core.AppPaths.DataDir, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string CurrentLogFile => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Write(string category, string message, Exception? exception = null)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {category}: {message}" +
                   (exception is null ? "" : Environment.NewLine + exception);
        Queue.Add(text);
    }

    private static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
            }
            catch
            {
                // діагностика не має ламати застосунок
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _minLevel);

    public void Dispose() { }

    private sealed class FileLogger(string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Write($"{logLevel} {category}", formatter(state, exception), exception);
        }
    }
}
