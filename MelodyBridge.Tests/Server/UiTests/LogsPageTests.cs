using Bunit;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TestContext = Bunit.TestContext;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Logs page UI with the real LogCollector and real LogAreas mapping:
/// entries render with friendly area labels, area chips filter, errors
/// surface in a banner, and export text contains the filtered entries.
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
    public void Logs_RendersEntriesWithFriendlyAreas()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "Added playlist Techno with 101 tracks");
        _collector.Log(LogLevel.Error, "MelodyBridge.Application.Services.DownloadManager", "no plugin could download this track");

        var cut = _ctx.Render<Logs>();

        Assert.That(cut.Markup, Does.Contain("Added playlist Techno with 101 tracks"));
        Assert.That(cut.Markup, Does.Contain("Playlists"), "raw type names must be shown as areas");
        Assert.That(cut.Markup, Does.Contain("Downloads"));
        Assert.That(cut.FindAll(".log-category").Select(c => c.TextContent),
            Does.Not.Contain("MelodyBridge.Infrastructure"),
            "the visible category cell shows the friendly area, not the raw namespace");
    }

    [Test]
    public void Logs_ErrorsSurfaceInBanner()
    {
        _collector.Log(LogLevel.Info, "Scanner", "scan done");
        _collector.Log(LogLevel.Error, "MelodyBridge.Application.Services.DownloadManager", "soundcloud download failed");

        var cut = _ctx.Render<Logs>();

        Assert.That(cut.Markup, Does.Contain("recent problem detected"),
            "the error banner must appear when errors exist");
        Assert.That(cut.Markup, Does.Contain("soundcloud download failed"));
    }

    [Test]
    public void Logs_NoErrors_NoBanner()
    {
        _collector.Log(LogLevel.Info, "Scanner", "all good");

        var cut = _ctx.Render<Logs>();

        Assert.That(cut.Markup, Does.Not.Contain("problem"));
        Assert.That(cut.Markup, Does.Contain("all good"));
    }

    [Test]
    public void Logs_AreaChipFiltersStream()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "playlist synced");
        _collector.Log(LogLevel.Info, "MelodyBridge.Application.Services.DownloadManager", "download finished");

        var cut = _ctx.Render<Logs>();

        var chips = cut.FindAll(".filter-chip");
        Assert.That(chips.Count, Is.EqualTo(LogAreas.All.Length + 1 + 4 + 1),
            "one chip per area plus All areas, plus Info+/Warning/Error/Debug plus All levels");

        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-area") == "Downloads").Click();

        Assert.That(cut.Markup, Does.Contain("download finished"));
        Assert.That(cut.Markup, Does.Not.Contain("playlist synced"),
            "selecting an area must hide other areas' entries");
    }

    [Test]
    public void Logs_LevelChipFiltersStream()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "playlist synced");
        _collector.Log(LogLevel.Warn, "MelodyBridge.Infrastructure.Services.PlaylistStore", "bitrate looks inflated");

        var cut = _ctx.Render<Logs>();

        // Warning only: the stream keeps the warning and hides the info row.
        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-level") == "Warning").Click();
        Assert.That(cut.Markup, Does.Contain("bitrate looks inflated"));
        Assert.That(cut.FindAll(".logs-list .log-row").Count(), Is.EqualTo(1),
            "Info must be hidden from the stream");
        Assert.That(cut.FindAll(".logs-list .log-level").Single().TextContent,
            Is.EqualTo("WARN"));

        // Error chip must cover Critical too.
        _collector.Log(LogLevel.Error, "MelodyBridge.Infrastructure.Services.PlaylistStore", "download failed hard");
        _collector.Log(LogLevel.Critical, "MelodyBridge.Infrastructure.Services.PlaylistStore", "fatal crash");
        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-level") == "Error").Click();

        var levels = cut.FindAll(".logs-list .log-level").Select(l => l.TextContent).ToList();
        Assert.That(levels, Is.EqualTo(new[] { "CRITICAL", "ERROR" }),
            "Error and Critical both count as the Error level, newest first");
    }

    [Test]
    public void Logs_SearchMatchesFriendlyAreaName()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "added Techno");
        _collector.Log(LogLevel.Info, "MelodyBridge.Application.Services.DownloadManager", "downloaded a file");

        var cut = _ctx.Render<Logs>();

        cut.Find("input[placeholder='Search text...']")
            .Input(new ChangeEventArgs { Value = "playlists" });

        Assert.That(cut.Markup, Does.Contain("added Techno"),
            "searching the friendly area name must find its entries");
    }

    [Test]
    public void Logs_SearchHidesNonMatchingEntries()
    {
        _collector.Log(LogLevel.Info, "Scanner", "scan of /music done");
        _collector.Log(LogLevel.Warn, "Downloader", "soundcloud search failed");

        var cut = _ctx.Render<Logs>();

        cut.Find("input[placeholder='Search text...']")
            .Input(new ChangeEventArgs { Value = "scanner" });

        Assert.That(cut.Markup, Does.Contain("scan of /music done"));
        Assert.That(cut.Markup, Does.Not.Contain("soundcloud search failed"));
    }

    [Test]
    public void Logs_DefaultInfoPlus_HidesDebugAndTrace()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Application.Services.DownloadManager", "an info row");
        _collector.Log(LogLevel.Warn, "MelodyBridge.Application.Services.DownloadManager", "a warn row");
        _collector.Log(LogLevel.Error, "MelodyBridge.Application.Services.DownloadManager", "an error row");
        _collector.Log(LogLevel.Debug, "MelodyBridge.Application.Services.DownloadManager", "a debug row");
        _collector.Log(LogLevel.Trace, "MelodyBridge.Application.Services.DownloadManager", "a trace row");

        var cut = _ctx.Render<Logs>();

        var text = cut.Find(".logs-list").TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("an info row"));
            Assert.That(text, Does.Contain("a warn row"));
            Assert.That(text, Does.Contain("an error row"));
            Assert.That(text, Does.Not.Contain("a debug row"),
                "the default level is Info+, so debug noise stays out");
            Assert.That(text, Does.Not.Contain("a trace row"));
        });

        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-level") == "Debug").Click();
        var debug = cut.Find(".logs-list").TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(debug, Does.Contain("a debug row"));
            Assert.That(debug, Does.Contain("a trace row"));
            Assert.That(debug, Does.Not.Contain("an info row"));
        });
    }

    [Test]
    public void Logs_AreaChipsShowTheirEntryCounts()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Application.Services.DownloadManager", "download one");
        _collector.Log(LogLevel.Info, "MelodyBridge.Application.Services.DownloadManager", "download two");
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "playlist one");

        var cut = _ctx.Render<Logs>();

        var downloads = cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-area") == "Downloads");
        var playlists = cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-area") == "Playlists");
        Assert.Multiple(() =>
        {
            Assert.That(downloads.QuerySelector(".chip-count")?.TextContent, Is.EqualTo("2"));
            Assert.That(playlists.QuerySelector(".chip-count")?.TextContent, Is.EqualTo("1"));
            Assert.That(downloads.GetAttribute("title"), Does.Contain("2 entries"));
        });
    }

    [Test]
    public void Logs_DevPanelDownloadCategory_LandsInDownloads()
    {
        _collector.Log(LogLevel.Info, "Download", "a dev panel download row");
        _collector.Log(LogLevel.Info, "Queue", "a dev panel queue row");

        var cut = _ctx.Render<Logs>();

        var downloads = cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-area") == "Downloads");
        Assert.That(downloads.QuerySelector(".chip-count")?.TextContent, Is.EqualTo("2"),
            "the Dev panel's short category names must not fall into System");

        downloads.Click();
        var text = cut.Find(".logs-list").TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("a dev panel download row"));
            Assert.That(text, Does.Contain("a dev panel queue row"));
        });
    }

    [Test]
    public void Logs_CopyReportsSuccess()
    {
        _collector.Log(LogLevel.Info, "Sync", "only entry to copy");
        _ctx.JSInterop.Setup<bool>("melody.copyText", _ => true).SetResult(true);

        var cut = _ctx.Render<Logs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Copy").Click();

        Assert.That(cut.Find(".result-box").TextContent, Does.Contain("Copied 1 entry to clipboard"));
    }

    [Test]
    public void Logs_CopyWithoutAClipboardOffersExportInstead()
    {
        _collector.Log(LogLevel.Info, "Sync", "only entry to copy");
        // Plain-http hosts have no navigator.clipboard, and the fallback can
        // still be denied: the page must survive it and point at Export.
        _ctx.JSInterop.Setup<bool>("melody.copyText", _ => true).SetResult(false);

        var cut = _ctx.Render<Logs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Copy").Click();

        Assert.That(cut.Find(".result-box").TextContent, Does.Contain("Use Export instead"));
    }

    [Test]
    public void Exporter_TextContainsBufferedEntries()
    {
        _collector.Log(LogLevel.Info, "Sync", "playlist Techno synced with 101 tracks");

        var text = _exporter.ExportToText();

        Assert.That(text, Does.Contain("playlist Techno synced with 101 tracks"));
        Assert.That(text, Does.Contain("INFO"));
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

    [Test]
    public void Logs_AreaChipRestoresWithAllAreas()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Application.Services.DownloadManager", "downloads area entry");
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "playlists area entry");

        var cut = _ctx.Render<Logs>();

        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-area") == "Downloads").Click();

        var rows = cut.FindAll(".logs-list .log-row");
        Assert.That(rows.Count(), Is.EqualTo(1), "only the downloads row stays");
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("downloads area entry"));
        Assert.That(cut.Find(".logs-list").TextContent, Does.Not.Contain("playlists area entry"));

        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-area") == "all").Click();

        var restored = cut.FindAll(".logs-list .log-row");
        Assert.That(restored.Count(), Is.EqualTo(2), "All areas must restore both rows");
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("downloads area entry"));
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("playlists area entry"));
    }

    [Test]
    public void Logs_LevelChipTogglesBackWithAllLevels()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "the info entry");
        _collector.Log(LogLevel.Warn, "MelodyBridge.Infrastructure.Services.PlaylistStore", "the warn entry");

        var cut = _ctx.Render<Logs>();

        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-level") == "Warning").Click();

        var warnRows = cut.FindAll(".logs-list .log-row");
        Assert.That(warnRows.Count(), Is.EqualTo(1), "only the warn row stays");
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("the warn entry"));
        Assert.That(cut.Find(".logs-list").TextContent, Does.Not.Contain("the info entry"));

        cut.FindAll(".filter-chip").Single(c => c.GetAttribute("data-level") == "all").Click();

        var restored = cut.FindAll(".logs-list .log-row");
        Assert.That(restored.Count(), Is.EqualTo(2), "All levels must restore both rows");
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("the info entry"));
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("the warn entry"));
    }

    [Test]
    public void Logs_NewestFirstToggleFlipsRowOrder()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "alpha is older");
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "beta is newer");

        var cut = _ctx.Render<Logs>();

        var firstBefore = cut.FindAll(".logs-list .log-row").First();
        Assert.That(firstBefore.TextContent, Does.Contain("beta is newer"),
            "newest first is the default order");

        cut.Find(".toggle-switch input[type='checkbox']").Change(false);

        var firstAfter = cut.FindAll(".logs-list .log-row").First();
        Assert.That(firstAfter.TextContent, Does.Contain("alpha is older"),
            "toggling off must flip to oldest first");
    }

    [Test]
    public void Logs_ShowOnlyProblemsButtonFiltersAndRestores()
    {
        _collector.Log(LogLevel.Info, "MelodyBridge.Infrastructure.Services.PlaylistStore", "harmless info entry");
        _collector.Log(LogLevel.Error, "MelodyBridge.Application.Services.DownloadManager", "explosive error entry");

        var cut = _ctx.Render<Logs>();

        cut.FindAll("button.btn-modern").Single(b => b.TextContent.Trim() == "Show only problems").Click();

        Assert.That(cut.FindAll(".logs-list .log-row").Count(), Is.EqualTo(1),
            "Show only problems must hide the info row from the stream");
        Assert.That(cut.Find(".logs-list").TextContent, Does.Contain("explosive error entry"));

        cut.FindAll("button.btn-modern").Single(b => b.TextContent.Trim() == "Show all").Click();

        var restored = cut.Find(".logs-list");
        Assert.That(restored.TextContent, Does.Contain("harmless info entry"),
            "Show all must bring the info row back");
    }
}
