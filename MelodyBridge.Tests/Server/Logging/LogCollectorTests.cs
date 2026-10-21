using MelodyBridge.Core.Logging;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Server.Logging;

/// <summary>
/// The collector is what every log line passes through, so it is also the
/// one place that stamps the area. These tests pin that contract: an area
/// is always stored, an explicit area wins, and Version only moves when
/// the buffer actually changes, which is what lets the Logs page idle.
/// </summary>
[TestFixture]
public class LogCollectorTests
{
    [Test]
    public void Log_StoresTheAreaDerivedFromTheCategory()
    {
        var collector = new LogCollector();

        collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "synced");

        var entry = collector.GetEntries().Single();
        Assert.That(entry.Area, Is.EqualTo("Playlists"));
    }

    [Test]
    public void Log_KeepsAnExplicitArea()
    {
        var collector = new LogCollector();

        collector.Log(LogLevel.Warn, "Some.New.Component", "careful",
            area: "Sources");

        Assert.That(collector.GetEntries().Single().Area, Is.EqualTo("Sources"),
            "a caller that knows its area must not be second-guessed by string matching");
    }

    [Test]
    public void Log_AlwaysLeavesAnAreaEvenForUnknownCategories()
    {
        var collector = new LogCollector();

        collector.Log(LogLevel.Info, "Totally.Unknown.Category", "hello");

        Assert.That(collector.GetEntries().Single().Area, Is.EqualTo("System"));
    }

    [Test]
    public void Version_MovesOnLogAndClearButNotOnRead()
    {
        var collector = new LogCollector();
        var start = collector.Version;

        collector.GetEntries();
        Assert.That(collector.Version, Is.EqualTo(start), "reading must not look like a change");

        collector.Log(LogLevel.Info, "Scanner", "one");
        Assert.That(collector.Version, Is.GreaterThan(start));

        var afterLog = collector.Version;
        collector.Clear();
        Assert.That(collector.Version, Is.GreaterThan(afterLog));
    }
}
