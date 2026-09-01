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
/// Live UI round-trip: render the real Playlists page, click "Add playlist",
/// type a real Spotify URL and submit. The page runs the real provider stack
/// (real HTTP) and the real SQLite store — no stubs anywhere.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("UI")]
public class PlaylistsLiveUITests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-live-ui-{Guid.NewGuid():N}.db");
        var factory = new SqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);

        var providers = new ISourceProvider[]
        {
            new SpotifySourceProvider(NullLogger<SpotifySourceProvider>.Instance),
        };

        // Download manager with zero plugins: the UI test covers fetching only.
        var downloadManager = new Application.Services.DownloadManager(
            new EmptyDownloaderRegistry(),
            NullLogger<Application.Services.DownloadManager>.Instance);

        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
            _ctx.Services.AddSingleton(tokenStore);
            _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
                tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
            _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
                tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
            var store = new PlaylistStore(factory, providers, downloadManager, NullLogger<PlaylistStore>.Instance);
        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton(new Application.Services.DownloadCoordinator(
            store, factory,
            NullLogger<Application.Services.DownloadCoordinator>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.SettingsStore(factory));
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
    public void AddPlaylistThroughUI_FetchesLiveSpotifyAndShowsSavedCard()
    {
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("No playlists yet")), TimeSpan.FromSeconds(5));

        // Open the add dialog.
        var addButton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add playlist");
        addButton.Click();
        Assert.That(cut.Markup, Does.Contain("Playlist link"));

        // Type a real public playlist URL and submit.
        var urlInput = cut.Find("input[placeholder^='https://open.spotify.com']");
        urlInput.Change("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");

        var submit = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Fetch & save");
        submit.Click();

        // Poll the SQLite file until the snapshot lands (bUnit renders
        // synchronously, so the DB is the reliable completion signal).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        PlaylistEntity? stored = null;
        while (DateTime.UtcNow < deadline)
        {
            using var db = new SqliteFactory(_dbPath).CreateDbContext();
            stored = db.Playlists.Include(p => p.Tracks).FirstOrDefault();
            if (stored is not null) break;
            Thread.Sleep(250);
        }

        Assert.That(stored, Is.Not.Null, "live fetch through the UI must persist the playlist");
        Assert.That(stored!.Name, Does.Contain("Today"), "expected Today's Top Hits metadata");
        Assert.That(stored.TrackCount, Is.GreaterThan(0));
        Assert.That(stored.ExternalId, Is.EqualTo("37i9dQZF1DXcBWIGoYBM5M"));
        Assert.That(stored.Tracks.All(t => !string.IsNullOrEmpty(t.ExternalId)), Is.True,
            "every stored track needs its Spotify ID");
        Assert.That(stored.Tracks.Select(t => t.Position), Is.EqualTo(Enumerable.Range(0, stored.TrackCount)),
            "stored tracks keep playlist order");
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

    private sealed class EmptyDownloaderRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
