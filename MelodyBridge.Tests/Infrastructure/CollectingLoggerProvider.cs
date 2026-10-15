using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Keeps the app's own log lines in memory for the duration of a test
/// host. A failing live test can then show what the plugins and the
/// waterfall actually reported, instead of only the aggregated reason a
/// track row carries.
/// </summary>
public sealed class CollectingLoggerProvider : ILoggerProvider
{
    private const int MaxLines = 5000;
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>The lines logged so far, oldest first.</summary>
    public IReadOnlyList<string> Snapshot() => _lines.ToArray();

    /// <summary>The most recent lines joined into one line, for a failure message.</summary>
    public string Tail(int count) => string.Join(" | ", _lines.ToArray().TakeLast(count));

    public ILogger CreateLogger(string categoryName) => new Collector(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Collector(string category, ConcurrentQueue<string> lines) : ILogger
    {
        private string ShortName => category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The app's own lines (and everything non-EF) at Debug; framework
        // noise from EF would drown the plugin diagnostics we are after.
        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Debug
               && !category.StartsWith("Microsoft", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null)
                message += $" ({exception.GetType().Name}: {exception.Message})";
            if (lines.Count >= MaxLines && lines.TryDequeue(out _)) { }
            lines.Enqueue($"{logLevel}: {ShortName}: {message}");
        }
    }
}
