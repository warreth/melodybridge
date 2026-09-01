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
        var downloadManager = new Application.Services.DownloadManager(
            new EmptyRegistry(),
            NullLogger<Application.Services.DownloadManager>.Instance);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
            _ctx.Services.AddSingleton(tokenStore);
            _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
                tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
            _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
                tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
            _ctx.Services.AddSingleton(new PlaylistStore(
            factory,
            Array.Empty<ISourceProvider>(),
            downloadManager,
            NullLogger<PlaylistStore>.Instance));
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
    public void Playlists_WithSavedPlaylist_ShowsRefreshButtonPerCard()
    {
        SeedOnePlaylist("Another Mix", 3, Platform.Spotify);

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Another Mix")), TimeSpan.FromSeconds(3));
        var refreshButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Refresh");
        Assert.That(refreshButtons.Count(), Is.EqualTo(1), "one refresh action per playlist card");
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
    }
}
