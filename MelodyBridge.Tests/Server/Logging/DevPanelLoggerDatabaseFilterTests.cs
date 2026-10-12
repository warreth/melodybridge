using MelodyBridge.Core.Logging;
using MelodyBridge.Server.Logging;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;

namespace MelodyBridge.Tests.Server.Logging;

/// <summary>
/// The EF Core command filter: by default the endless Executed
/// DbCommand lines must not reach the Logs page, with the switch on
/// they must, and no other category is ever affected. The tests drive
/// the real DevPanelLogger into a real collector.
/// </summary>
[TestFixture]
public class DevPanelLoggerDatabaseFilterTests
{
    private static LogCollector NewCollector() => new();

    private DevPanelLogger LoggerFor(ILogCollector collector, string category)
    {
        var provider = new DevPanelLoggerProvider(collector);
        return (DevPanelLogger)provider.CreateLogger(category);
    }

    private static LogEntry? EntryFor(ILogCollector collector, string messageStart)
        => collector.GetEntries().FirstOrDefault(e => e.Message.StartsWith(messageStart, StringComparison.Ordinal));

    [Test]
    public void EfCommandLines_AreHiddenByDefault()
    {
        DatabaseLogSwitch.Set(false);
        var collector = NewCollector();
        var logger = LoggerFor(collector, "Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogInformation("Executed DbCommand (1ms) SELECT * FROM Tracks");

        Assert.That(EntryFor(collector, "Executed DbCommand"), Is.Null,
            "with the toggle off the SQL noise must never reach the Logs page");
    }

    [Test]
    public void EfCommandLines_AppearWhenEnabled()
    {
        DatabaseLogSwitch.Set(true);
        try
        {
            var collector = NewCollector();
            var logger = LoggerFor(collector, "Microsoft.EntityFrameworkCore.Database.Command");

            logger.LogInformation("Executed DbCommand (1ms) SELECT * FROM Tracks");

            var entry = EntryFor(collector, "Executed DbCommand");
            Assert.That(entry, Is.Not.Null,
                "with the toggle on the SQL lines land in the Logs page for debugging");
            Assert.That(entry!.Level, Is.EqualTo(CoreLogLevel.Info));
        }
        finally
        {
            DatabaseLogSwitch.Set(false);
        }
    }

    [Test]
    public void EfCommandWarnings_AreNeverHidden()
    {
        DatabaseLogSwitch.Set(false);
        var collector = NewCollector();
        var logger = LoggerFor(collector, "Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogWarning("EF is retrying a failed command");

        Assert.That(EntryFor(collector, "EF is retrying"), Is.Not.Null,
            "only the info-level SQL chatter is filtered; warnings from EF stay visible");
    }

    [Test]
    public void OtherCategories_AreNeverHidden()
    {
        DatabaseLogSwitch.Set(false);
        var collector = NewCollector();
        var logger = LoggerFor(collector, "MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider");

        logger.LogInformation("Spotify account connected");

        Assert.That(EntryFor(collector, "Spotify account connected"), Is.Not.Null,
            "the filter only touches EF command categories, nothing else");
    }

    /// <summary>
    /// The real composition: a LoggerFactory with the appsettings filter
    /// (EF command pinned to Warning for the console's sake) and the dev
    /// panel provider. The provider-specific rule must keep the EF chatter
    /// flowing to the panel so the Advanced toggle can decide.
    /// </summary>
    [Test]
    public void EfCommandLines_SurviveTheAppsettingsFilter_AndFollowTheSwitch()
    {
        DatabaseLogSwitch.Set(false);
        var collector = NewCollector();
        using var factory = LoggerFactory.Create(builder =>
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Information",
                    ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
                })
                .Build();
            // AddConfiguration expects the Logging section, exactly like
            // the host does with appsettings.json.
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddDevPanel(collector);
        });
        var logger = factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogInformation("Executed DbCommand (1ms) SELECT 1");
        Assert.That(EntryFor(collector, "Executed DbCommand"), Is.Null,
            "with the toggle off the panel hides the chatter");

        DatabaseLogSwitch.Set(true);
        try
        {
            logger.LogInformation("Executed DbCommand (2ms) SELECT 2");
            Assert.That(EntryFor(collector, "Executed DbCommand"), Is.Not.Null,
                "with the toggle on the panel must receive what the factory filter would drop");
        }
        finally
        {
            DatabaseLogSwitch.Set(false);
        }

        // Warnings pass in both states.
        logger.LogWarning("EF is retrying a failed command");
        Assert.That(EntryFor(collector, "EF is retrying"), Is.Not.Null);
    }
}
