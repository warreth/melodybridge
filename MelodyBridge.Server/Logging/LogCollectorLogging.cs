using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Logging;

/// <summary>
/// Wires the collector logger the way the app needs it. The Logs page has
/// its own DatabaseLogSwitch for EF command chatter, but the LoggerFactory's
/// global rules (appsettings pins Microsoft.EntityFrameworkCore.Database.Command
/// to Warning so the console stays readable) would drop those lines before
/// any provider sees them. A provider-specific rule gives the collector the
/// raw stream; the switch decides what actually reaches the Logs page.
/// </summary>
public static class LogCollectorLogging
{
    public static ILoggingBuilder AddLogCollector(this ILoggingBuilder logging, Core.Logging.ILogCollector collector)
    {
        logging.AddProvider(new LogCollectorLoggerProvider(collector));
        logging.AddFilter<LogCollectorLoggerProvider>(
            "Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Trace);
        return logging;
    }
}
