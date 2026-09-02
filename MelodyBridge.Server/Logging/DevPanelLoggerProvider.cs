using MelodyBridge.Core.Logging;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Server.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that forwards all <see cref="ILogger{T}"/> calls
/// to the application-wide <see cref="ILogCollector"/>. This bridges the standard
/// .NET logging pipeline into the DevPanel in-memory buffer so providers, services,
/// and controllers all appear in the DevPanel log viewer automatically.
///
/// Register in the host builder:
/// <code>
/// builder.Logging.AddProvider(new DevPanelLoggerProvider(logCollector));
/// </code>
/// </summary>
public sealed class DevPanelLoggerProvider : ILoggerProvider
{
    private readonly ILogCollector _collector;

    public DevPanelLoggerProvider(ILogCollector collector)
    {
        _collector = collector;
    }

    public ILogger CreateLogger(string categoryName) =>
        new DevPanelLogger(_collector, categoryName);

    public void Dispose() { }
}

/// <summary>
/// The actual <see cref="ILogger"/> that writes to <see cref="ILogCollector"/>.
/// Public for the logging filter tests: they drive real entries through
/// the real logger, no stubs.
/// </summary>
public sealed class DevPanelLogger : ILogger
{
    private readonly ILogCollector _collector;
    private readonly string _categoryName;

    private static readonly Dictionary<Microsoft.Extensions.Logging.LogLevel, CoreLogLevel> LevelMap = new()
    {
        [Microsoft.Extensions.Logging.LogLevel.Trace] = CoreLogLevel.Trace,
        [Microsoft.Extensions.Logging.LogLevel.Debug] = CoreLogLevel.Debug,
        [Microsoft.Extensions.Logging.LogLevel.Information] = CoreLogLevel.Info,
        [Microsoft.Extensions.Logging.LogLevel.Warning] = CoreLogLevel.Warn,
        [Microsoft.Extensions.Logging.LogLevel.Error] = CoreLogLevel.Error,
        [Microsoft.Extensions.Logging.LogLevel.Critical] = CoreLogLevel.Critical,
    };

    public DevPanelLogger(ILogCollector collector, string categoryName)
    {
        _collector = collector;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
        logLevel != Microsoft.Extensions.Logging.LogLevel.None;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        // EF Core command logs are debug noise by default: one or two
        // lines per SQL query, drowning everything else on the Logs page.
        // The Advanced page turns them on only when hunting a database
        // problem. Warnings and errors from EF always pass: only the
        // info-level chatter is gated.
        if (!DatabaseLogSwitch.Enabled
            && _categoryName.StartsWith(DatabaseLogSwitch.EfCommandPrefix, StringComparison.Ordinal)
            && logLevel is Microsoft.Extensions.Logging.LogLevel.Trace
                or Microsoft.Extensions.Logging.LogLevel.Debug
                or Microsoft.Extensions.Logging.LogLevel.Information)
            return;

        var level = LevelMap.TryGetValue(logLevel, out var mapped) ? mapped : CoreLogLevel.Info;
        var message = formatter(state, exception);
        var detail = exception?.ToString();

        _collector.Log(level, _categoryName, message, detail);
    }
}
