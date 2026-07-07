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
/// </summary>
internal sealed class DevPanelLogger : ILogger
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

        var level = LevelMap.TryGetValue(logLevel, out var mapped) ? mapped : CoreLogLevel.Info;
        var message = formatter(state, exception);
        var detail = exception?.ToString();

        _collector.Log(level, _categoryName, message, detail);
    }
}
