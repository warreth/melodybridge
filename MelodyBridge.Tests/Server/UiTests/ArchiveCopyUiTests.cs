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
/// The archive copy UI on the playlist details page, against a real
/// SQLite store: the Archive copy group renders with its folder input,
/// the format select appears only when an archive folder exists
/// (playlist override or global default), the track table shows the
/// archive chip and the archive failure warning pill, and a typed
/// folder input persists through the 500ms debounced save.
/// </summary>
[TestFixture]
[Category("UI")]
public class ArchiveCopyUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-arc-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        Seed();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            _factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
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

    private void Seed(Action<PlaylistEntity>? extra = null)
    {
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var playlist = new PlaylistEntity
        {
            Id = "pl-1",
            Name = "Archive UI test",
            SourceUrl = "https://example.com/pl",
            TargetDirectory = "/tmp",
            PreferredFormat = "auto",
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "m1",
                    Title = "Archived track",
                    Artist = "Artist",
                    Position = 0,
                    DownloadStatus = "downloaded",
                    CurrentPath = "/tmp/archived-track.mp3",
                },
                new()
                {
                    MelodyId = "m2",
                    Title = "Warning track",
                    Artist = "Artist",
                    Position = 1,
                    DownloadStatus = "downloaded",
                    CurrentPath = "/tmp/warning-track.mp3",
                    Warning = "archive copy failed: ffmpeg not found on this host",
                },
            },
        };
        extra?.Invoke(playlist);
        db.Playlists.Add(playlist);
        db.SaveChanges();
    }

    private IRenderedComponent<PlaylistDetails> RenderPage()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Archive copy")), TimeSpan.FromSeconds(3));
        return cut;
    }

    /// <summary>The archive folder input: the one under the Archive copy eyebrow.</summary>
    private static IElement ArchiveFolderInput(IRenderedComponent<PlaylistDetails> cut)
    {
        var group = cut.FindAll(".archive-group").Single();
        return group.QuerySelectorAll("label")
            .Single(l => l.TextContent.Contains("Archive folder"))
            .QuerySelector("input")!;
    }

    private string DbArchiveDirectory()
    {
        using var db = _factory.CreateDbContext();
        return _factory.CreateDbContext().Playlists.AsNoTracking()
            .First(p => p.Id == "pl-1").ArchiveDirectory ?? "";
    }

    [Test]
    public void ArchiveGroup_RendersFolderInputAndDisabledFormatSelect_WithoutAnyFolder()
    {
        // Neither a playlist ArchiveDirectory nor a global archive_path:
        // the group still renders, the format select stays away.
        var cut = RenderPage();

        Assert.That(cut.FindAll(".archive-group").Count, Is.EqualTo(1),
            "the Archive copy group renders inside the sync configuration panel");
        Assert.That(cut.Markup, Does.Contain("Archive folder"),
            "the folder input carries its label");
        Assert.That(cut.Markup, Does.Not.Contain("Archive format"),
            "with no folder anywhere the format select is absent");
        Assert.That(cut.Markup, Does.Contain("Leave empty for no archive copy."),
            "the hint explains what an empty folder means");
    }

    [Test]
    public void FormatSelect_Shown_WhenGlobalArchivePathSet()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = "/archive-global" });
            db.SaveChanges();
        }

        var cut = RenderPage();

        var group = cut.FindAll(".archive-group").Single();
        Assert.That(group.TextContent, Does.Contain("Archive format"),
            "the global default alone reveals the format select");
        var select = group.QuerySelector("select")!;
        Assert.That(select.TextContent, Does.Contain("Opus"));
        Assert.That(select.TextContent, Does.Contain("FLAC"));
        Assert.That(select.TextContent, Does.Contain("MP3"));
        Assert.That(select.TextContent, Does.Contain("AAC"));
        Assert.That(cut.Markup, Does.Contain("/archive-global"),
            "the global default shows as the placeholder hint");
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".archive-group select")
                .Single().HasAttribute("disabled"), Is.False,
                "the global archive path keeps the select enabled"),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public void ArchiveFolderInput_Prefilled_FromPlaylistOverride()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Find("pl-1")!.ArchiveDirectory = "/playlist-archive";
            db.SaveChanges();
        }

        var cut = RenderPage();

        var input = ArchiveFolderInput(cut);
        Assert.That(input.GetAttribute("value"), Is.EqualTo("/playlist-archive"),
            "the playlist override prefills the folder input");
        Assert.That(cut.FindAll(".archive-group").Single().TextContent,
            Does.Contain("Archive format"),
            "the playlist's own archive folder reveals the format select");
    }

    [Test]
    public void ArchiveChip_ShowsArchiveFilename_WhenFileColumnEnabled()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Find("pl-1")!.ArchiveDirectory = "/playlist-archive";
            var track = db.Tracks.First(t => t.MelodyId == "m1");
            track.ArchivePath = "/playlist-archive/archived-track.opus";
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "show_filename", Value = "true" });
            db.SaveChanges();
        }

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("archived-track.opus")),
            TimeSpan.FromSeconds(3));
        var chip = cut.Find("span.archive-chip");
        Assert.That(chip.TextContent.Trim(), Is.EqualTo("archive: archived-track.opus"),
            "the chip names the archive file next to the primary one");
        Assert.That(chip.GetAttribute("title"), Is.EqualTo("/playlist-archive/archived-track.opus"),
            "hovering the chip shows the full archive path");
    }

    [Test]
    public void ArchiveChip_Absent_WhenFileColumnDisabled()
    {
        using (var db = _factory.CreateDbContext())
        {
            var track = db.Tracks.First(t => t.MelodyId == "m1");
            track.ArchivePath = "/playlist-archive/archived-track.opus";
            db.SaveChanges();
        }

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Archived track")), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("archive-chip"),
            "without the File column the archive chip must not render");
    }

    [Test]
    public void ArchiveFailureWarning_RendersWarnPillWithTheReason()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Warning track")), TimeSpan.FromSeconds(3));
        var pills = cut.FindAll("span.pill.warn");
        Assert.That(pills.Count, Is.EqualTo(1),
            "exactly the archive-failed track carries the warning pill");
        Assert.That(pills[0].GetAttribute("title"),
            Is.EqualTo("archive copy failed: ffmpeg not found on this host"),
            "the pill tooltip spells out the archive failure");
    }

    [Test]
    public async Task ArchiveFolderInput_DebouncedSave_PersistsToTheDbRow()
    {
        var cut = RenderPage();

        var input = ArchiveFolderInput(cut);
        Assert.That(input.GetAttribute("value"), Is.Empty,
            "the fresh playlist starts with no archive folder");
        input.Input("/tmp/archive-of-doom");

        // Yield-poll past the 500ms debounce: the continuation lands on
        // the renderer's sync context, which WaitForAssertion does not pump.
        for (var i = 0; i < 40 && DbArchiveDirectory() != "/tmp/archive-of-doom"; i++)
            await Task.Delay(100);
        Assert.That(DbArchiveDirectory(), Is.EqualTo("/tmp/archive-of-doom"),
            "the debounced save persisted the archive folder");

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("save-state saved")), TimeSpan.FromSeconds(3));
    }
}
