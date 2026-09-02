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

[TestFixture]
[Category("UI")]
public class PlaylistsPageTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-ui-{Guid.NewGuid():N}.db");
        var factory = new SqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
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

    [Test]
    public void Playlists_EmptyDb_ShowsEmptyState()
    {
        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("No playlists yet")), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("Add playlist"));
    }

    [Test]
    public void Playlists_WithSavedPlaylist_ShowsCardWithMetadata()
    {
        SeedOnePlaylist("Test Mix", 12, Platform.Spotify);

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Test Mix"));
            Assert.That(cut.Markup, Does.Contain("12 tracks"));
            Assert.That(cut.Markup, Does.Contain("Spotify"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_WithSavedPlaylist_CardLinksToDetails()
    {
        SeedOnePlaylist("Another Mix", 3, Platform.Spotify);

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Another Mix")), TimeSpan.FromSeconds(3));
        var card = cut.Find(".playlist-card");
        Assert.That(card.TagName, Is.EqualTo("DIV"),
            "the card is a plain div now");
        var links = card.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        Assert.That(links, Has.All.Contains("playlists/"),
            "the cover and the title link to the playlist page");
        Assert.That(links.Count, Is.EqualTo(2),
            "only the cover and the title are clickable");
    }

    [Test]
    public void Playlists_ImportModal_OpensWithAllThreeRoutes()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Markup, Does.Contain("Import your music"));
        Assert.That(cut.Markup, Does.Contain("Exportify CSV"), "file route 1");
        Assert.That(cut.Markup, Does.Contain("Spotify data export"), "file route 2");
        // No account is connected in the test context, so the account
        // route shows its connect hint instead of its import buttons.
        Assert.That(cut.Markup, Does.Contain("Connect it in Settings"), "account route");
    }

    [Test]
    public void Playlists_ImportModal_ExplainsPremiumRequirement()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Markup, Does.Contain("Spotify Premium"),
            "the account route must say it needs Premium");
        Assert.That(cut.Markup, Does.Contain("works with a free account"),
            "the file routes must say they work without Premium");
        Assert.That(cut.Markup, Does.Contain("exportify.net"),
            "the recommended tool must be linked");
    }

    [Test]
    public void Playlists_ImportModal_TakeoutCard_SaysAlwaysManual()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Markup, Does.Contain("Always manual, never automatic"),
            "the Spotify data export must be labelled as manual");
        Assert.That(cut.Markup, Does.Contain("YourLibrary.json"),
            "must name the file the user should upload");
    }

    [Test]
    public void Playlists_ImportModal_RecommendsExportifyForFreeUsers()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        var recommended = cut.FindAll("span.pill.ok")
            .Any(p => p.TextContent.Trim() == "recommended");
        Assert.That(recommended, Is.True,
            "the Exportify card carries the recommended pill");
    }

    [Test]
    public void Playlists_AddForm_ScheduleOptions_MatchTheSyncJobWordedSelect()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.primary").Click();

        // Same six options the Library schedule picker and the sync job
        // wizard offer: a named select, not a bare checkbox.
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Auto-sync schedule"));
            foreach (var option in new[] { "Manual", "Hourly", "Daily", "Weekly", "Monthly", "Cron" })
                Assert.That(cut.Markup, Does.Contain($">{option}</option>"),
                    $"the schedule select offers {option}");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_Card_ShowsSizeOnDiskAndSyncSchedule()
    {
        SeedOnePlaylist("Sized Mix", 2, Platform.Spotify);

        // Give the two tracks real disk facts: one finished file.
        var factory = new SqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            var tracks = db.Tracks.ToList();
            tracks[0].DownloadStatus = "downloaded";
            tracks[0].FileSizeBytes = 5 * 1024 * 1024;
            db.SaveChanges();
        }

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("5 MB on disk"),
                "the card adds up the finished files and shows the size");
            Assert.That(cut.Markup, Does.Contain("· manual"),
                "the card states the sync schedule like the sync jobs do");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_ImportModal_IsWideEnoughForThePlaylistsTable()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Find(".wizard-modal").ClassList, Does.Contain("wide"),
            "the import modal carries the table, so it gets the wide variant");
    }

    private void SeedOnePlaylist(string name, int trackCount, Platform platform)
    {
        var factory = new SqliteFactory(_dbPath);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        var playlist = new PlaylistEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            SourcePlatform = platform,
            TrackCount = trackCount,
            LastSyncStatus = SyncStatus.Completed,
        };
        for (var i = 0; i < trackCount; i++)
        {
            playlist.Tracks.Add(new TrackEntity
            {
                MelodyId = $"mb-{i}",
                Title = $"Track {i}",
                Artist = "Artist",
                Position = i,
                DownloadStatus = "pending",
            });
        }
        db.Playlists.Add(playlist);
        db.SaveChanges();
    }

    private sealed class SqliteFactory(string dbPath) : IDbContextFactory<MelodyBridgeDbContext>
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

    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
