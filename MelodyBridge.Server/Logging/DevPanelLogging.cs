using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Logging;

/// <summary>
/// Wires the Dev panel logger the way the app needs it. The panel has its
/// own DatabaseLogSwitch for EF command chatter, but the LoggerFactory's
/// global rules (appsettings pins Microsoft.EntityFrameworkCore.Database.Command
/// to Warning so the console stays readable) would drop those lines before
/// any provider sees them. A provider-specific rule gives the panel the
/// raw stream; the switch decides what actually reaches the Logs page.
/// </summary>
public static class DevPanelLogging
{
    public static ILoggingBuilder AddDevPanel(this ILoggingBuilder logging, Core.Logging.ILogCollector collector)
    {
        logging.AddProvider(new DevPanelLoggerProvider(collector));
        logging.AddFilter<DevPanelLoggerProvider>(
            "Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Trace);
        return logging;
    }
}
