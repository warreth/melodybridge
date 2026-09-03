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
/// Removing a playlist from the UI: the card grows a remove button, the
/// confirm dialog explains what happens, and the choice about the music
/// files is honoured in the real database.
/// </summary>
[TestFixture]
[Category("UI")]
public class PlaylistDeleteUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private string _musicDir = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-del-{Guid.NewGuid():N}.db");
        _musicDir = Path.Combine(Path.GetTempPath(), $"mb-del-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_musicDir);
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        // The page injects both account providers for the import panel.
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory,
            NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
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
        try { Directory.Delete(_musicDir, true); } catch { /* best effort */ }
    }

    /// <summary>One playlist with one downloaded file that really exists.</summary>
    private string SeedWithFile()
    {
        var file = Path.Combine(_musicDir, "track.mp3");
        File.WriteAllText(file, "not really an mp3");

        using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Id = "pl-del",
            Name = "Doomed Mix",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TrackCount = 1,
        };
        playlist.Tracks.Add(new TrackEntity
        {
            MelodyId = "m-del",
            Title = "The end",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "downloaded",
            FileSizeBytes = 1000,
            CurrentPath = file,
        });
        db.Playlists.Add(playlist);
        db.SaveChanges();
        return file;
    }

    private IRenderedComponent<Playlists> RenderLoaded()
    {
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Doomed Mix")), TimeSpan.FromSeconds(3));
        return cut;
    }

    [Test]
    public void Card_ShowsRemoveButton_OpensDialogWithFileChoice()
    {
        SeedWithFile();
        var cut = RenderLoaded();

        var remove = cut.FindAll("button[title='Remove this playlist']");
        Assert.That(remove.Count, Is.EqualTo(1), "each playlist card offers removal");
        remove[0].Click();

        Assert.That(cut.Markup, Does.Contain("Remove 'Doomed Mix'?"),
            "the dialog names the playlist");
        Assert.That(cut.Markup, Does.Contain("Delete the downloaded music files too"),
            "the file choice is explicit, not a surprise");
        Assert.That(cut.Markup, Does.Contain("1 file"),
            "the dialog counts the files it would delete");
    }

    [Test]
    public void Cancel_KeepsPlaylistAndFile()
    {
        var file = SeedWithFile();
        var cut = RenderLoaded();

        cut.Find("button[title='Remove this playlist']").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            Assert.That(db.Playlists.Count(), Is.EqualTo(1), "cancel keeps the playlist row");
            Assert.That(db.Tracks.Count(), Is.EqualTo(1), "cancel keeps the track row");
        }
        Assert.That(File.Exists(file), Is.True, "cancel keeps the file");
    }

    [Test]
    public void Confirm_WithoutFiles_RemovesRow_KeepsFile()
    {
        var file = SeedWithFile();
        var cut = RenderLoaded();

        cut.Find("button[title='Remove this playlist']").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove playlist").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("The music files stay on disk")),
            TimeSpan.FromSeconds(3));
        using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
        Assert.That(db.Playlists.Count(), Is.EqualTo(0), "the playlist row is gone");
        Assert.That(db.Tracks.Count(), Is.EqualTo(0), "the track rows go with it");
        Assert.That(File.Exists(file), Is.True, "the music file itself survives");
    }

    [Test]
    public void Confirm_WithFiles_RemovesRowAndFile()
    {
        var file = SeedWithFile();
        var cut = RenderLoaded();

        cut.Find("button[title='Remove this playlist']").Click();
        // Tick the "delete the files too" switch, then confirm.
        cut.Find(".confirm-modal input[type='checkbox']").Change(true);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove playlist").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(File.Exists(file), Is.False, "the file goes with the playlist");
            using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
            Assert.That(db.Playlists.Count(), Is.EqualTo(0), "and the row is gone too");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Details_DeleteButton_NavigatesBackToOverview()
    {
        SeedWithFile();
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-del"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("The end")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Delete playlist").Click();
        Assert.That(cut.Markup, Does.Contain("Delete 'Doomed Mix'?"),
            "the details page uses the same dialog");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Delete playlist"
            && b.ClassList.Contains("danger")).Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
            Assert.That(db.Playlists.Count(), Is.EqualTo(0), "deleted from the details page");
        }, TimeSpan.FromSeconds(3));
        // bunit's NavigationManager: assert the URI moved to the overview.
        Assert.That(_ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri,
            Does.EndWith("/playlists"),
            "deleting from the details page lands back on the overview");
    }

    private sealed class TestSqliteFactory(string dbPath) : IDbContextFactory<MelodyBridgeDbContext>
    {
        public MelodyBridgeDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new MelodyBridgeDbContext(options);
        }

        public Task<MelodyBridgeDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
