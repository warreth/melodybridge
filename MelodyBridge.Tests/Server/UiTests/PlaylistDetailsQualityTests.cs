using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// PlaylistDetails page quality UI: the preset selector reflects the
/// stored quality string, real measured facts appear per track in their
/// own Bitrate/Rate/Format columns, the optional File column shows the
/// filename, and the inflation warning pill shows for flagged files.
/// </summary>
[TestFixture]
[Category("UI")]
public class PlaylistDetailsQualityTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-q-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "Quality UI test",
                SourceUrl = "https://example.com/pl",
                TargetDirectory = "/tmp",
                PreferredFormat = "mp3:192",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1",
                        Title = "Fine track",
                        Artist = "Artist",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        Bitrate = 320,
                        SampleRateHz = 44100,
                        FileSizeBytes = 9_000_000,
                        MediaType = "mp3",
                        CurrentPath = "/tmp/fine-track.mp3",
                    },
                    new()
                    {
                        MelodyId = "m2",
                        Title = "Doubtful track",
                        Artist = "Artist",
                        Position = 1,
                        DownloadStatus = "downloaded",
                        Warning = "bitrate looks inflated: spectral ceiling 12.2 kHz: matches a 128 kbps source",
                    },
                },
            });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    /// <summary>The first data row of the track table, parsed from the DOM.</summary>
    private static IElement RowFor(IRenderedComponent<PlaylistDetails> cut, string title)
        => cut.FindAll("table tbody tr")
            .Single(tr => tr.QuerySelector("td strong")?.TextContent == title);

    /// <summary>Maps header names to cell indexes so assertions read by column.</summary>
    private static Dictionary<string, int> HeaderIndexes(IRenderedComponent<PlaylistDetails> cut)
        => cut.FindAll("table thead th")
            .Select((th, i) => (name: th.TextContent.Trim(), i))
            .Where(t => t.name.Length > 0)
            .ToDictionary(t => t.name, t => t.i);

    [Test]
    public void QualitySelector_ShowsStoredPreset()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Audio quality")), TimeSpan.FromSeconds(3));
        // Two selects now: container and bitrate cap. The stored "mp3:192"
        // must land as MP3 + 192 kbps.
        var selects = cut.FindAll("select");
        Assert.That(selects.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(cut.Markup, Does.Contain("<option value=\"mp3\""),
            "the container select must carry the stored MP3 choice");
        Assert.That(cut.Markup, Does.Contain("<option value=\"192\""),
            "the bitrate select must offer the stored 192 kbps cap");
    }

    [Test]
    public void ScheduleSelect_SavesThePickedPreset()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto-sync schedule")), TimeSpan.FromSeconds(3));

        // Same six options everywhere; pick Weekly and save.
        var select = cut.FindAll("select").First(s => s.TextContent.Contains("Cron"));
        Assert.That(select.TextContent, Does.Contain("Manual"));
        Assert.That(select.TextContent, Does.Contain("Hourly"));
        Assert.That(select.TextContent, Does.Contain("Daily"));
        Assert.That(select.TextContent, Does.Contain("Monthly"));

        select.Change("Weekly");
        cut.Render();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save settings").Click();
        cut.Render();

        using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
        var saved = db.Playlists.Find("pl-1")!;
        Assert.That(saved.ScheduleCron, Is.EqualTo("0 3 * * 1"),
            "the Weekly preset lands as its cron equivalent in the DB");
        Assert.That(saved.AutoSyncEnabled, Is.True,
            "the legacy boolean stays in sync for older readers");
    }

    [Test]
    public void TrackTable_ShowsFileSizeColumn()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fine track")), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("<th>Size</th>"),
            "the track table has a size column");
        Assert.That(cut.Markup, Does.Contain("8.6 MB"),
            "the 9,000,000 byte file renders as 8.6 MB next to the track");
    }

    [Test]
    public void TrackTable_SplitsQualityIntoSeparateColumns()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fine track")), TimeSpan.FromSeconds(3));

        var headers = cut.FindAll("table thead th").Select(th => th.TextContent.Trim()).ToList();
        Assert.That(headers, Does.Contain("Bitrate"), "bitrate is its own column");
        Assert.That(headers, Does.Contain("Rate"), "sample rate is its own column");
        Assert.That(headers, Does.Contain("Format"), "the container is its own column");
        Assert.That(headers, Does.Not.Contains("Quality"),
            "the old combined Quality column is gone");

        // Parse the cells of the measured track by header index.
        var index = HeaderIndexes(cut);
        var cells = RowFor(cut, "Fine track").QuerySelectorAll("td");
        Assert.That(cells[index["Bitrate"]].TextContent.Trim(), Is.EqualTo("320 kbps"),
            "the measured bitrate sits in the Bitrate cell");
        Assert.That(cells[index["Rate"]].TextContent.Trim(), Is.EqualTo("44.1 kHz"),
            "the measured sample rate sits in the Rate cell");
        Assert.That(cells[index["Format"]].TextContent.Trim(), Is.EqualTo("MP3"),
            "the upper-cased container sits in the Format cell");
    }

    [Test]
    public void TrackTable_UnmeasuredTrack_ShowsDashInQualityColumns()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Doubtful track")), TimeSpan.FromSeconds(3));

        var index = HeaderIndexes(cut);
        var cells = RowFor(cut, "Doubtful track").QuerySelectorAll("td");
        // The track is marked downloaded but was never measured, so every
        // fact column renders the dash.
        Assert.That(cells[index["Bitrate"]].TextContent.Trim(), Is.EqualTo("-"));
        Assert.That(cells[index["Rate"]].TextContent.Trim(), Is.EqualTo("-"));
        Assert.That(cells[index["Format"]].TextContent.Trim(), Is.EqualTo("-"));
    }

    [Test]
    public void TrackTable_ShowsFilenameInOwnCell_WhenSettingOn()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "show_filename", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("fine-track.mp3")), TimeSpan.FromSeconds(3));

        var headers = cut.FindAll("table thead th").Select(th => th.TextContent.Trim()).ToList();
        Assert.That(headers, Does.Contain("File"),
            "the filename toggle adds its own column");

        var index = HeaderIndexes(cut);
        var cell = RowFor(cut, "Fine track").QuerySelectorAll("td")[index["File"]];
        Assert.That(cell.TextContent.Trim(), Is.EqualTo("fine-track.mp3"),
            "the filename lives in the File cell, not under the title");
        Assert.That(cell.QuerySelector("span")?.GetAttribute("title"),
            Is.EqualTo("/tmp/fine-track.mp3"),
            "hovering shows the full path");
    }

    [Test]
    public void TrackTable_HasNoFileColumn_WhenSettingOff()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fine track")), TimeSpan.FromSeconds(3));

        var headers = cut.FindAll("table thead th").Select(th => th.TextContent.Trim()).ToList();
        Assert.That(headers, Does.Not.Contains("File"),
            "with show_filename off the column disappears entirely");
        Assert.That(cut.Markup, Does.Not.Contain("fine-track.mp3"),
            "the filename must not leak into the title cell either");
    }

    [Test]
    public void TrackTable_ShowsWarningPill_ForFlaggedTrack()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Doubtful track")), TimeSpan.FromSeconds(3));

        var warned = cut.FindAll("span.pill.warn");
        Assert.That(warned.Count, Is.EqualTo(1),
            "exactly the flagged track shows the warning pill");
        Assert.That(warned[0].GetAttribute("title"), Does.Contain("inflated"),
            "hovering the pill explains the doubt");
    }

    [Test]
    public void TrackTable_ShowsInflatedSummaryHint()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fine track")), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("look inflated"),
            "the playlist summary explains how to fix inflated files");
    }

    [Test]
    public void DownloadButtons_ReachTheCoordinator()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Download missing")), TimeSpan.FromSeconds(3));

        var coordinator = _ctx.Services.GetRequiredService<Application.Services.DownloadCoordinator>();
        var buttons = cut.FindAll("button").Where(b => b.TextContent.Contains("Download missing"));
        Assert.That(buttons.Count(), Is.EqualTo(1));
        buttons.First().Click();

        cut.WaitForAssertion(() =>
            Assert.That(coordinator.IsActive("pl-1"), Is.True),
            TimeSpan.FromSeconds(3));
    }
}
