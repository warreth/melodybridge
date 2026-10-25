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
        _ctx.Services.AddPluginAuthMonitor();
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new EmptyRegistry());
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

    [Test]
    public void ArchiveToggle_OnByDefault_AndSwitchingOff_HidesFolderAndFormat()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = "/archive-global" });
            db.SaveChanges();
        }

        var cut = RenderPage();

        var toggle = cut.FindAll(".archive-section input[type='checkbox']").Single();
        Assert.That(toggle.HasAttribute("checked"), Is.True,
            "the archive copy is on by default");
        Assert.That(cut.Markup, Does.Contain("Archive folder"),
            "an archived playlist shows its folder and format settings");

        toggle.Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Archive folder"),
                "switching the archive off hides the folder field");
            Assert.That(cut.Markup, Does.Not.Contain("Archive format"),
                "and the format select with it");
            Assert.That(DbArchiveEnabled(), Is.False,
                "the switch persisted for this playlist");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void ArchiveToggle_Off_HidesTheArchiveFields_OnReload()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = "/archive-global" });
            db.Playlists.Find("pl-1")!.ArchiveEnabled = false;
            db.SaveChanges();
        }

        var cut = RenderPage();

        Assert.That(cut.Markup, Does.Not.Contain("Archive folder"));
        Assert.That(cut.Markup, Does.Not.Contain("Archive format"));
        var toggle = cut.FindAll(".archive-section input[type='checkbox']").Single();
        Assert.That(toggle.HasAttribute("checked"), Is.False);
    }

    [Test]
    public void ArchiveFormat_DefaultOption_NamesTheAppDefault()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = "/archive-global" });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_format", Value = "flac" });
            db.SaveChanges();
        }

        var cut = RenderPage();

        var select = cut.FindAll(".archive-section select").Single();
        var first = select.QuerySelectorAll("option")[0];
        Assert.That(first.TextContent.Trim(), Is.EqualTo("App default (FLAC)"),
            "the select spells out what the app default currently converts to");
    }

    [Test]
    public void DefaultFolders_ShowAsEmptyFields_SoThePlaylistFolderApplies()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "music_path", Value = "/music" });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = "/archive" });
            var playlist = db.Playlists.Find("pl-1")!;
            playlist.TargetDirectory = "/music";
            playlist.ArchiveDirectory = "/archive";
            db.SaveChanges();
        }

        var cut = RenderPage();

        var downloadInput = cut.FindAll("label")
            .Single(l => l.TextContent.Contains("Download folder"))
            .QuerySelector("input")!;
        Assert.That(downloadInput.GetAttribute("value"), Is.Empty,
            "a stored path equal to the app-wide default is not an explicit choice");
        Assert.That(downloadInput.GetAttribute("placeholder"), Is.EqualTo("/music"));

        var archiveInput = ArchiveFolderInput(cut);
        Assert.That(archiveInput.GetAttribute("value"), Is.Empty,
            "the same rule for the archive folder");
        Assert.That(archiveInput.GetAttribute("placeholder"), Is.EqualTo("/archive"),
            "the placeholder is the archive root; the playlist folder shows as a grey suffix");
        Assert.That(cut.Markup, Does.Contain("{playlist name}"),
            "the suffix says the playlist folder is appended");
        Assert.That(cut.Markup, Does.Contain("/archive/Archive UI test"),
            "the hint names the effective per-playlist archive folder");
    }

    private bool? DbArchiveEnabled()
    {
        using var db = _factory.CreateDbContext();
        return db.Playlists.AsNoTracking().First(p => p.Id == "pl-1").ArchiveEnabled;
    }

    /// <summary>The archive folder input: the one under the Archive copy eyebrow.</summary>
    private static IElement ArchiveFolderInput(IRenderedComponent<PlaylistDetails> cut)
    {
        var group = cut.FindAll(".archive-section").Single();
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

        Assert.That(cut.FindAll(".archive-section").Count, Is.EqualTo(1),
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

        var group = cut.FindAll(".archive-section").Single();
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
            Assert.That(cut.FindAll(".archive-section select")
                .Single().HasAttribute("disabled"), Is.False,
                "the global archive path keeps the select enabled"),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ArchiveFormat_CustomContainer_SavesAndReloads()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Find("pl-1")!.ArchiveDirectory = "/playlist-archive";
            db.SaveChanges();
        }
        var cut = RenderPage();

        cut.FindAll(".archive-section select").Single().Change("custom");
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Custom archive container")),
            TimeSpan.FromSeconds(3));

        cut.Find(".archive-section input[placeholder='ogg']").Input("ogg");
        Assert.That(cut.Find(".archive-section input[placeholder='ogg']").GetAttribute("value"),
            Is.EqualTo("ogg"), "the typed container is in the field");

        // The debounce continuation runs on the renderer's sync context,
        // which WaitForAssertion does not pump.
        string? Stored()
        {
            using var db = _factory.CreateDbContext();
            return db.Playlists.Find("pl-1")?.ArchiveFormat;
        }
        for (var i = 0; i < 40 && Stored() != "ogg"; i++)
            await Task.Delay(100);
        Assert.That(Stored(), Is.EqualTo("ogg"),
            "the typed container persists as the playlist override");

        var reloaded = RenderPage();
        var select = reloaded.FindAll(".archive-section select").Single();
        Assert.Multiple(() =>
        {
            Assert.That(select.GetAttribute("value"), Is.EqualTo("custom"));
            Assert.That(reloaded.Find(".archive-section input[placeholder='ogg']")
                .GetAttribute("value"), Is.EqualTo("ogg"));
        });
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
        Assert.That(cut.FindAll(".archive-section").Single().TextContent,
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
    public void ArchiveCheckPill_AppearsAfterTheBackgroundProbe()
    {
        // The page stats the archive files off the render path (a stat per
        // row on a network mount made the page crawl): the pill shows up
        // with the verdict instead of during the first render.
        var archiveFile = Path.Combine(Path.GetTempPath(), $"mb-arc-probe-{Guid.NewGuid():N}.opus");
        File.WriteAllText(archiveFile, "archive bytes");
        try
        {
            using (var db = _factory.CreateDbContext())
            {
                db.Playlists.Find("pl-1")!.ArchiveDirectory = "/playlist-archive";
                db.Tracks.First(t => t.MelodyId == "m1").ArchivePath = archiveFile;
                db.SaveChanges();
            }

            var cut = RenderPage();

            cut.WaitForAssertion(() =>
            {
                var pill = cut.FindAll("span.pill")
                    .FirstOrDefault(p => p.GetAttribute("title")?.StartsWith("Archive copy present") == true);
                Assert.That(pill, Is.Not.Null,
                    "the probe verdict must reach the table without a click");
                Assert.That(pill!.ClassList, Does.Contain("ok"));
                Assert.That(pill.GetAttribute("title"), Does.Contain(archiveFile));
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { File.Delete(archiveFile); } catch { /* best effort */ }
        }
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

    // ── Archive dialog: the pill opens the create/retry action ─────

    /// <summary>The archive dialog's primary button label, or null when
    /// no archive dialog is open.</summary>
    private static string? ArchiveDialogAction(IRenderedComponent<PlaylistDetails> cut)
    {
        var modal = cut.FindAll(".confirm-modal").FirstOrDefault();
        return modal?.QuerySelectorAll(".wizard-footer button").LastOrDefault()?.TextContent.Trim();
    }

    [Test]
    public void ArchiveMinusPill_Click_OpensDialog_WithTheCreateAction()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = "/archive-global" });
            db.SaveChanges();
        }
        var cut = RenderPage();

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Archived track"));
        var minus = row.QuerySelectorAll("span.pill")
            .Single(p => p.GetAttribute("title")?.Contains("No archive copy") == true);
        var button = minus.Ancestors().OfType<IElement>().First(e => e.TagName == "BUTTON");
        Assert.That(button.GetAttribute("class"), Does.Contain("pill-button"),
            "the minus pill is clickable when the page offers the archive action");
        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No archive copy yet"));
            Assert.That(ArchiveDialogAction(cut), Is.EqualTo("Create archive copy"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void WarningPill_Click_OpensTheArchiveDialog_WithTheFailureText()
    {
        var cut = RenderPage();

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Warning track"));
        var bang = row.QuerySelector("span.pill.warn")!;
        var button = bang.Ancestors().OfType<IElement>().First(e => e.TagName == "BUTTON");
        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("archive copy failed: ffmpeg not found on this host"),
                "the dialog repeats the real failure cause");
            Assert.That(ArchiveDialogAction(cut), Is.EqualTo("Create archive copy"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void ArchiveDialog_Create_WritesTheCopyAndCloses()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mb-arc-dialog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var primary = Path.Combine(root, "track.mp3");
        File.WriteAllBytes(primary, new byte[] { 9, 8, 7 });
        try
        {
            using (var db = _factory.CreateDbContext())
            {
                db.DownloaderSettings.Add(new DownloaderSettingEntity
                    { Key = "archive_path", Value = Path.Combine(root, "archive") });
                db.DownloaderSettings.Add(new DownloaderSettingEntity
                    { Key = "archive_format", Value = "auto" });
                db.Tracks.Single(t => t.MelodyId == "m2").Warning = null;
                db.Tracks.Single(t => t.MelodyId == "m2").CurrentPath = primary;
                db.SaveChanges();
            }
            var cut = RenderPage();

            var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Warning track"));
            row.QuerySelector("button.pill-button")!.Click();
            cut.WaitForAssertion(() =>
                Assert.That(ArchiveDialogAction(cut), Is.EqualTo("Create archive copy")),
                TimeSpan.FromSeconds(3));

            cut.FindAll(".confirm-modal .wizard-footer button")
                .Single(b => b.TextContent.Contains("Create archive copy")).Click();

            cut.WaitForAssertion(() =>
            {
                using var db = _factory.CreateDbContext();
                var track = db.Tracks.AsNoTracking().Single(t => t.MelodyId == "m2");
                Assert.That(track.ArchivePath, Is.Not.Null.And.Not.Empty,
                    "the click created the copy and persisted its path");
                Assert.That(File.Exists(track.ArchivePath), Is.True);
                Assert.That(cut.FindAll(".confirm-modal"), Is.Empty, "the dialog closes on success");
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
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
