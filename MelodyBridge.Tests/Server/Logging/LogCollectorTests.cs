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
    public void GetExportEntries_KeepsErrorsThatLeftTheLiveBuffer()
    {
        var collector = new LogCollector(maxEntries: 2, maxProblemEntries: 10);

        collector.Log(LogLevel.Error, "Sync", "the error worth keeping");
        for (var i = 0; i < 4; i++)
            collector.Log(LogLevel.Info, "Sync", $"chatter {i}");

        Assert.That(collector.GetEntries().Any(e => e.Message == "the error worth keeping"),
            Is.False, "the live buffer rolled over");
        var export = collector.GetExportEntries();
        Assert.That(export.Select(e => e.Message), Does.Contain("the error worth keeping"));
        Assert.That(export.Select(e => e.Sequence), Is.Unique,
            "an entry in both buffers is exported once");
        Assert.That(export.Select(e => e.Sequence), Is.Ordered.Descending,
            "newest first");
    }

    [Test]
    public void ProblemArchive_IsBounded()
    {
        var collector = new LogCollector(maxEntries: 2, maxProblemEntries: 3);
        for (var i = 0; i < 6; i++)
            collector.Log(LogLevel.Error, "Sync", $"problem {i}");

        var problems = collector.GetExportEntries()
            .Where(e => e.Message.StartsWith("problem ", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToList();
        Assert.That(problems, Has.Count.EqualTo(3), "runaway warnings stay bounded");
        Assert.That(problems, Does.Contain("problem 5"));
    }

    [Test]
    public void Export_TextIncludesArchivedErrors()
    {
        var collector = new LogCollector(maxEntries: 2, maxProblemEntries: 10);
        var exporter = new LogExporter(collector);
        collector.Log(LogLevel.Error, "Sync", "archived error");
        for (var i = 0; i < 3; i++)
            collector.Log(LogLevel.Info, "Sync", $"info {i}");

        var text = exporter.ExportToText();

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("archived error"),
                "the export keeps the error after the live buffer forgot it");
            Assert.That(text, Does.Contain("Entries:   3"));
            Assert.That(text, Does.Contain("Live cap:  2"));
        });
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
