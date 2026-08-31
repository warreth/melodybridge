using Bunit;
using MelodyBridge.Core.Logging;
using MelodyBridge.Server.Components.Pages;
using Microsoft.AspNetCore.Components;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using TestContext = Bunit.TestContext;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Logs page UI with the real LogCollector: entries must render with
/// level/category, filtering must hide, and export must produce text
/// containing the entries.
/// </summary>
[TestFixture]
public class LogsPageTests
{
    private TestContext _ctx = null!;
    private LogCollector _collector = null!;
    private LogExporter _exporter = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _collector = new LogCollector(maxEntries: 500);
        _exporter = new LogExporter(_collector);
        _ctx.Services.AddSingleton<ILogCollector>(_collector);
        _ctx.Services.AddSingleton(_exporter);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Logs_RendersEntriesWithLevelAndCategory()
    {
        _collector.Log(LogLevel.Info, "DownloaderRegistry", "Downloader ytdlp enabled");
        _collector.Log(LogLevel.Error, "DownloadManager", "no plugin could download this track");

        var cut = _ctx.Render<Logs>();

        Assert.That(cut.Markup, Does.Contain("Downloader ytdlp enabled"));
        Assert.That(cut.Markup, Does.Contain("no plugin could download this track"));
        Assert.That(cut.Markup, Does.Contain("DownloaderRegistry"));
        Assert.That(cut.Markup, Does.Contain("ERROR"), "level must be visible");
    }

    [Test]
    public void Logs_EmptyCollector_ShowsEmptyState()
    {
        var cut = _ctx.Render<Logs>();
        Assert.That(cut.Markup, Does.Contain("No log entries"));
    }

    [Test]
    public void Logs_FilterBySearch_HidesNonMatchingEntries()
    {
        _collector.Log(LogLevel.Info, "Scanner", "scan of /music done");
        _collector.Log(LogLevel.Warn, "Downloader", "soundcloud search failed");

        var cut = _ctx.Render<Logs>();

        var search = cut.Find("input[placeholder='Search text...']");
        search.Input(new ChangeEventArgs { Value = "scanner" });

        Assert.That(cut.Markup, Does.Contain("scan of /music done"));
        Assert.That(cut.Markup, Does.Not.Contain("soundcloud search failed"),
            "non-matching entries must disappear");
    }

    [Test]
    public void Exporter_TextContainsBufferedEntries()
    {
        _collector.Log(LogLevel.Info, "Sync", "playlist Techno synced with 101 tracks");

        var text = _exporter.ExportToText();

        Assert.That(text, Does.Contain("playlist Techno synced with 101 tracks"));
        Assert.That(text, Does.Contain("INFO"));
        Assert.That(text, Does.Contain("Sync"));
    }

    [Test]
    public void Collector_ClearRemovesAllEntries()
    {
        _collector.Log(LogLevel.Info, "A", "one");
        _collector.Log(LogLevel.Warn, "B", "two");
        Assert.That(_collector.GetEntries(), Has.Count.EqualTo(2));

        _collector.Clear();

        Assert.That(_collector.GetEntries(), Is.Empty);
    }

    [Test]
    public void Collector_BoundedAtMaxEntries()
    {
        var small = new LogCollector(maxEntries: 5);
        for (var i = 0; i < 12; i++)
            small.Log(LogLevel.Info, "Test", $"entry {i}");

        var entries = small.GetEntries();
        Assert.That(entries, Has.Count.EqualTo(5), "buffer must stay bounded");
        // GetEntries returns newest first; with 12 written and 5 kept, the
        // buffer holds entries 7..11 and entry 11 is the newest.
        Assert.That(entries[0].Message, Does.Contain("entry 11"));
        Assert.That(entries[^1].Message, Does.Contain("entry 7"),
            "oldest entries are evicted first");
    }
}
