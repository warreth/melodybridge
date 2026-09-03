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
/// File imports (Exportify CSV, Spotify privacy export) are manual
/// snapshots: the UI must say so, and must not offer refresh or
/// auto-sync. Real SQLite behind every render.
/// </summary>
[TestFixture]
[Category("UI")]
public class ImportPlaylistUiTests
{
    private Bunit.TestContext _ctx = null!;
    private TestSqliteFactory _factory = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-import-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(_factory,
            NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    /// <summary>Import through the real store, exactly like the page does.</summary>
    private async Task<PlaylistEntity> ImportAsync(string name, params (string title, string artist)[] tracks)
    {
        var store = _ctx.Services.GetRequiredService<PlaylistStore>();
        var file = new ImportedFile("exportify",
        [
            new ImportedPlaylist(name, null, tracks.Select(t => new Track
            {
                Title = t.title,
                Artist = t.artist,
                SourcePlatform = Platform.Spotify,
                SyncStatus = SyncStatus.Pending,
            }).ToList()),
        ]);
        await store.ImportFileAsync(file, Path.Combine(Path.GetTempPath(), $"mb-import-{Guid.NewGuid():N}"));
        using var db = _factory.CreateDbContext();
        return db.Playlists.Include(p => p.Tracks).Single(p => p.Name == name);
    }

    [Test]
    public async Task ImportedPlaylist_ShowsAsImported_NotSpotify()
    {
        await ImportAsync("Summer 2024", ("Ride It", "Regard"));

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".playlist-card, .content-grid > div")
                .FirstOrDefault(d => d.TextContent.Contains("Summer 2024"));
            Assert.That(card, Is.Not.Null, "the imported playlist renders a card");
            Assert.That(card!.TextContent, Does.Contain("Imported"),
                "the card eyebrow says where this list came from");
            Assert.That(card.QuerySelector(".eyebrow")!.TextContent.Trim(),
                Is.EqualTo("Imported"), "the eyebrow is the label, not Spotify");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ImportedPlaylist_HasNoRefreshOrSchedule()
    {
        var playlist = await ImportAsync("Summer 2024", ("Ride It", "Regard"));

        // Details page: no refresh button, no schedule picker, no source link.
        var details = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, playlist.Id));
        details.WaitForAssertion(() =>
        {
            Assert.That(details.Markup, Does.Not.Contain(">Refresh<"),
                "an import has nothing to refresh from");
            Assert.That(details.Markup, Does.Not.Contain("Auto-sync schedule"),
                "auto-sync needs a live source");
            Assert.That(details.Markup, Does.Contain("re-import the file"),
                "the page says how updates work instead");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ImportedPlaylist_Refresh_ThrowsInsteadOfPhantomSync()
    {
        var playlist = await ImportAsync("Summer 2024", ("Ride It", "Regard"));
        var store = _ctx.Services.GetRequiredService<PlaylistStore>();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RefreshAsync(playlist.Id));
        Assert.That(ex!.Message.ToLowerInvariant(), Does.Contain("re-import the file"),
            "the error tells the user what to do");
    }

    [Test]
    public async Task ImportedPlaylist_IsNeverDueForAutoSync()
    {
        var playlist = await ImportAsync("Summer 2024", ("Ride It", "Regard"));
        using (var db = _factory.CreateDbContext())
        {
            var row = db.Playlists.Single(p => p.Id == playlist.Id);
            row.ScheduleCron = "0 * * * *"; // even with a schedule set by an older release
            db.SaveChanges();
        }

        var store = _ctx.Services.GetRequiredService<PlaylistStore>();
        var due = await store.GetDueForAutoSyncAsync();

        Assert.That(due.Any(p => p.Id == playlist.Id), Is.False,
            "imported playlists are never auto-synced, whatever their row says");
    }

    [Test]
    public async Task ImportTwice_UpdatesSameRow_NoDuplicate()
    {
        await ImportAsync("Summer 2024", ("Ride It", "Regard"));
        await ImportAsync("Summer 2024", ("Ride It", "Regard"), ("Circles", "Post Malone"));

        using var db = _factory.CreateDbContext();
        var rows = db.Playlists.Where(p => p.Name == "Summer 2024").ToList();
        Assert.That(rows, Has.Exactly(1).Items, "re-import must update, not duplicate");
        Assert.That(rows[0].TrackCount, Is.EqualTo(2), "the track list follows the file");
    }

    [Test]
    public async Task SchemaPatcher_MovesOldImportRows_ToUnknown()
    {
        // An import made before this change: Spotify platform + import URL.
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "legacy-1",
                Name = "Old import",
                SourceUrl = "spotify:import:old-import",
                SourcePlatform = Platform.Spotify,
                ExternalId = "old-import",
            });
            db.SaveChanges();
        }

        await SchemaPatcher.PatchAsync(_factory.CreateDbContext());

        using var verify = _factory.CreateDbContext();
        var row = verify.Playlists.Single(p => p.Id == "legacy-1");
        Assert.That(row.SourcePlatform, Is.EqualTo(Platform.Unknown),
            "the patcher moves legacy import rows so they stop acting like Spotify playlists");
        Assert.That(row.IsManualImport, Is.True);
    }
}
