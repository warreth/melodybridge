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
/// The archive destination checklist on the playlist details page, against
/// a real SQLite store: the default archive and named profiles are choices,
/// a selection persists as archive profile ids, and the track table still
/// shows the archive chip, probe verdict and archive action.
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

    private void SeedDefaultArchive(string path = "/archive-global", string format = "auto")
    {
        using var db = _factory.CreateDbContext();
        db.DownloaderSettings.Add(new DownloaderSettingEntity
            { Key = "archive_path", Value = path });
        db.DownloaderSettings.Add(new DownloaderSettingEntity
            { Key = "archive_format", Value = format });
        db.SaveChanges();
    }

    private ArchiveProfileEntity SeedProfile(string name, string path, string format)
    {
        using var db = _factory.CreateDbContext();
        var profile = new ArchiveProfileEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            RootPath = path,
            Format = format,
            Enabled = true,
        };
        db.ArchiveProfiles.Add(profile);
        db.SaveChanges();
        return profile;
    }

    private IRenderedComponent<PlaylistDetails> RenderPage()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Archive copies")), TimeSpan.FromSeconds(3));
        return cut;
    }

    private string? DbArchiveProfileIds()
    {
        using var db = _factory.CreateDbContext();
        return db.Playlists.AsNoTracking().First(p => p.Id == "pl-1").ArchiveProfileIds;
    }

    [Test]
    public void ArchiveProfiles_DefaultAndNamedDestinations_AreAChoiceList()
    {
        SeedDefaultArchive("/archive-global", "flac");
        SeedProfile("Opus mirror", "/archive-opus", "opus:128");

        var cut = RenderPage();

        var choices = cut.FindAll(".archive-choice");
        Assert.Multiple(() =>
        {
            Assert.That(choices, Has.Count.EqualTo(2));
            Assert.That(choices[0].TextContent, Does.Contain("Default archive"));
            Assert.That(choices[0].TextContent, Does.Contain("/archive-global"));
            Assert.That(choices[0].TextContent, Does.Contain("FLAC"));
            Assert.That(choices[1].TextContent, Does.Contain("Opus mirror"));
            Assert.That(choices[1].TextContent, Does.Contain("Opus 128 kbps"));
            Assert.That(choices.All(c => c.QuerySelector("input[type='checkbox']")
                .HasAttribute("checked")), Is.True,
                "a legacy playlist selects every destination by default");
        });
    }

    [Test]
    public void ArchiveProfiles_DeselectOne_SavesAnExplicitSelection()
    {
        SeedDefaultArchive();
        var profile = SeedProfile("Opus mirror", "/archive-opus", "opus:128");
        var cut = RenderPage();

        var choice = cut.FindAll(".archive-choice")
            .Single(c => c.TextContent.Contains("Opus mirror"));
        choice.QuerySelector("input[type='checkbox']")!.Change(false);

        cut.WaitForAssertion(() =>
        {
            var stored = DbArchiveProfileIds();
            Assert.That(stored, Is.Not.Null, "the first edit writes an explicit selection");
            Assert.That(stored, Does.Contain(PlaylistArchiveSelection.DefaultId));
            Assert.That(stored, Does.Not.Contain(profile.Id));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void ArchiveProfiles_EmptySelection_ShowsNoCopies()
    {
        SeedDefaultArchive();
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Find("pl-1")!.ArchiveProfileIds = "[]";
            db.SaveChanges();
        }

        var cut = RenderPage();

        Assert.That(cut.Markup, Does.Contain("No archive copies for this playlist."));
        Assert.That(cut.FindAll(".archive-choice input[type='checkbox']")
            .Any(c => c.HasAttribute("checked")), Is.False);
    }

    [Test]
    public void ArchiveProfiles_LegacyDisabled_SelectsNone()
    {
        SeedDefaultArchive();
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Find("pl-1")!.ArchiveEnabled = false;
            db.SaveChanges();
        }

        var cut = RenderPage();

        Assert.That(cut.FindAll(".archive-choice input[type='checkbox']")
            .Any(c => c.HasAttribute("checked")), Is.False);
        Assert.That(cut.Markup, Does.Contain("No archive copies for this playlist."));
    }

    [Test]
    public void ArchiveProfiles_LegacyOverride_IsVisibleAndCanBeDeselected()
    {
        using (var db = _factory.CreateDbContext())
        {
            var playlist = db.Playlists.Find("pl-1")!;
            playlist.ArchiveDirectory = "/legacy-archive";
            playlist.ArchiveFormat = "flac";
            db.SaveChanges();
        }

        var cut = RenderPage();
        var legacy = cut.FindAll(".archive-choice")
            .Single(choice => choice.TextContent.Contains("Old playlist archive"));
        Assert.Multiple(() =>
        {
            Assert.That(legacy.TextContent, Does.Contain("/legacy-archive"));
            Assert.That(legacy.TextContent, Does.Contain("FLAC"));
            Assert.That(legacy.QuerySelector("input[type='checkbox']")!.HasAttribute("checked"),
                Is.True, "the old override is the effective default until it is cleared");
        });

        legacy.QuerySelector("input[type='checkbox']")!.Change(false);

        cut.WaitForAssertion(() =>
        {
            using var db = _factory.CreateDbContext();
            var stored = db.Playlists.AsNoTracking().Single(p => p.Id == "pl-1");
            Assert.Multiple(() =>
            {
                Assert.That(stored.ArchiveDirectory, Is.Null);
                Assert.That(stored.ArchiveFormat, Is.Null);
                Assert.That(stored.ArchiveProfileIds, Is.EqualTo("[]"));
            });
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void ArchiveProfiles_LegacyOverride_WinsOverTheGlobalDefault()
    {
        SeedDefaultArchive("/archive-global", "opus:128");
        using (var db = _factory.CreateDbContext())
        {
            var playlist = db.Playlists.Find("pl-1")!;
            playlist.ArchiveDirectory = "/legacy-archive";
            playlist.ArchiveFormat = "flac";
            db.SaveChanges();
        }

        var cut = RenderPage();

        var choices = cut.FindAll(".archive-choice");
        Assert.Multiple(() =>
        {
            Assert.That(choices, Has.Count.EqualTo(1),
                "the legacy row replaces the global default until the checklist changes");
            Assert.That(choices[0].TextContent, Does.Contain("Old playlist archive"));
            Assert.That(choices[0].TextContent, Does.Contain("/legacy-archive"));
            Assert.That(choices[0].TextContent, Does.Contain("FLAC"));
            Assert.That(choices[0].TextContent, Does.Not.Contain("/archive-global"));
            Assert.That(choices[0].QuerySelector("input[type='checkbox']")!.HasAttribute("checked"),
                Is.True);
        });
        Assert.That(cut.Markup, Does.Contain("No archive copy for this track yet"),
            "the legacy archive still owns the track row's archive check");
    }

    [Test]
    public void ArchiveProfiles_NamedOnly_HidesTheDefaultArchiveCheck()
    {
        var profile = SeedProfile("Opus mirror", "/archive-opus", "opus:128");
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Find("pl-1")!.ArchiveProfileIds =
                PlaylistArchiveSelection.Serialize(new[] { profile.Id });
            db.SaveChanges();
        }

        var cut = RenderPage();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("No archive copy for this track yet"),
                "the row check describes the default archive, not a named profile copy");
            Assert.That(cut.Markup, Does.Not.Contain("No archive copies for this playlist."),
                "the named profile is still a destination");
        });
    }

    [Test]
    public void ArchiveProfiles_NoDestinations_ShowsTheSetupHint()
    {
        var cut = RenderPage();

        Assert.That(cut.Markup, Does.Contain("No archive profiles yet."));
        Assert.That(cut.FindAll(".archive-choice"), Is.Empty);
    }

    [Test]
    public void ArchiveChip_ShowsArchiveFilename_WhenFileColumnEnabled()
    {
        SeedDefaultArchive();
        using (var db = _factory.CreateDbContext())
        {
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
        Assert.That(chip.TextContent.Trim(), Is.EqualTo("archive: archived-track.opus"));
        Assert.That(chip.GetAttribute("title"), Is.EqualTo("/playlist-archive/archived-track.opus"));
    }

    [Test]
    public void ArchiveChip_Absent_WhenFileColumnDisabled()
    {
        SeedDefaultArchive();
        using (var db = _factory.CreateDbContext())
        {
            db.Tracks.First(t => t.MelodyId == "m1").ArchivePath = "/playlist-archive/archived-track.opus";
            db.SaveChanges();
        }

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Archived track")), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("archive-chip"));
    }

    [Test]
    public void ArchiveCheckPill_AppearsAfterTheBackgroundProbe()
    {
        SeedDefaultArchive();
        var archiveFile = Path.Combine(Path.GetTempPath(), $"mb-arc-probe-{Guid.NewGuid():N}.opus");
        File.WriteAllText(archiveFile, "archive bytes");
        try
        {
            using (var db = _factory.CreateDbContext())
            {
                db.Tracks.First(t => t.MelodyId == "m1").ArchivePath = archiveFile;
                db.SaveChanges();
            }

            var cut = RenderPage();

            cut.WaitForAssertion(() =>
            {
                var pill = cut.FindAll("span.pill")
                    .FirstOrDefault(p => p.GetAttribute("title")?.StartsWith("Archive copy present") == true);
                Assert.That(pill, Is.Not.Null);
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
        SeedDefaultArchive();
        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Warning track")), TimeSpan.FromSeconds(3));
        var pills = cut.FindAll("span.pill.warn");
        Assert.That(pills.Count, Is.EqualTo(1));
        Assert.That(pills[0].GetAttribute("title"),
            Is.EqualTo("archive copy failed: ffmpeg not found on this host"));
    }

    [Test]
    public void ArchiveMinusPill_Click_OpensDialog_WithTheCreateAction()
    {
        SeedDefaultArchive();
        var cut = RenderPage();

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Archived track"));
        var minus = row.QuerySelectorAll("span.pill")
            .Single(p => p.GetAttribute("title")?.Contains("No archive copy") == true);
        var button = minus.Ancestors().OfType<IElement>().First(e => e.TagName == "BUTTON");
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
        SeedDefaultArchive();
        var cut = RenderPage();

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Warning track"));
        var bang = row.QuerySelector("span.pill.warn")!;
        var button = bang.Ancestors().OfType<IElement>().First(e => e.TagName == "BUTTON");
        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("archive copy failed: ffmpeg not found on this host"));
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
            SeedDefaultArchive(Path.Combine(root, "archive"), "auto");
            using (var db = _factory.CreateDbContext())
            {
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
                Assert.That(track.ArchivePath, Is.Not.Null.And.Not.Empty);
                Assert.That(File.Exists(track.ArchivePath), Is.True);
                Assert.That(cut.FindAll(".confirm-modal"), Is.Empty);
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? ArchiveDialogAction(IRenderedComponent<PlaylistDetails> cut)
    {
        var modal = cut.FindAll(".confirm-modal").FirstOrDefault();
        return modal?.QuerySelectorAll(".wizard-footer button").LastOrDefault()?.TextContent.Trim();
    }
}
