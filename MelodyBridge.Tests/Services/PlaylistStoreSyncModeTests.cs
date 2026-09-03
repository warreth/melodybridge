using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// SyncMode behavior with a stubbed source provider and real SQLite:
/// Additive keeps removed tracks as flagged history, Mirror deletes them,
/// MelodyId/download state survive re-syncs.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class PlaylistStoreSyncModeTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-sync-{test}-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactory(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory,
        ISourceProvider provider)
        => new(factory,
            new[] { provider },
            new Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    /// <summary>Stub provider that serves a mutable playlist: simulates source changes.</summary>
    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;

        private readonly List<Track> _tracks;
        public string? LastRequestedIdentifier;

        public StubSourceProvider(params (string id, string title, string artist)[] tracks)
        {
            _tracks = tracks.Select(t => new Track
            {
                Title = t.title,
                Artist = t.artist,
                SongID = new SongID(Platform.Spotify, t.id),
                PlatformSongID = new SongID(Platform.Spotify, t.id),
            }).ToList();
        }

        public void RemoveTrack(string id) => _tracks.RemoveAll(t => t.SongID!.ID == id);

        public bool CanHandle(string sourceIdentifier) => sourceIdentifier.Contains("stub:", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
        {
            LastRequestedIdentifier = sourceIdentifier;
            return Task.FromResult(new Playlist
            {
                Id = "stub-playlist",
                Name = "Stub Playlist",
                Tracks = _tracks.ToList(),
            });
        }

        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
    }

    [Test]
    public async Task AdditiveMode_TrackRemovedFromSource_StaysAsFlaggedHistory()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var provider = new StubSourceProvider(
                ("t1", "Song One", "Artist A"),
                ("t2", "Song Two", "Artist B"));
            var store = NewStore(factory, provider);

            var first = await store.AddOrRefreshAsync("stub:playlist");
            var t1MelodyId = first.Tracks.Single(t => t.ExternalId == "t1").MelodyId;

            // Mark t1 as downloaded so we can verify state survives.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var track = db.Tracks.Single(t => t.ExternalId == "t1");
                track.DownloadStatus = "downloaded";
                track.CurrentPath = "/music/song1.mp3";
                await db.SaveChangesAsync();
            }

            // Source removes t1.
            provider.RemoveTrack("t1");
            await store.AddOrRefreshAsync("stub:playlist");

            await using var db2 = await factory.CreateDbContextAsync();
            var allTracks = await db2.Tracks.AsNoTracking().ToListAsync();
            var playlist = await db2.Playlists.Include(p => p.Tracks).AsNoTracking()
                .SingleAsync(p => p.Id == first.Id);

            Assert.That(allTracks.Count, Is.EqualTo(2), "additive keeps both rows (1 live + 1 history)");
            Assert.That(playlist.Tracks.Count, Is.EqualTo(1), "live snapshot only has the surviving track");

            var flagged = allTracks.Single(t => t.ExternalId == "t1");
            Assert.That(flagged.DownloadStatus, Is.EqualTo("removed-from-source"),
                "removed track must be flagged");
            Assert.That(flagged.MelodyId, Is.EqualTo(t1MelodyId), "identity survives removal");
            Assert.That(flagged.CurrentPath, Is.EqualTo("/music/song1.mp3"), "file path survives removal");

            var live = allTracks.Single(t => t.ExternalId == "t2");
            Assert.That(live.DownloadStatus, Is.EqualTo("pending"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task MirrorMode_TrackRemovedFromSource_IsDeleted()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var provider = new StubSourceProvider(
                ("t1", "Song One", "Artist A"),
                ("t2", "Song Two", "Artist B"));
            var store = NewStore(factory, provider);

            var first = await store.AddOrRefreshAsync("stub:playlist");
            await store.UpdateSettingsAsync(first.Id, null, false, null, null, PlaylistSyncMode.Mirror);

            provider.RemoveTrack("t1");
            await store.AddOrRefreshAsync("stub:playlist");

            await using var db = await factory.CreateDbContextAsync();
            var allTracks = await db.Tracks.AsNoTracking().ToListAsync();

            Assert.That(allTracks.Count, Is.EqualTo(1), "mirror deletes removed tracks entirely");
            Assert.That(allTracks[0].ExternalId, Is.EqualTo("t2"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task Resync_SurvivingTrackKeepsMelodyIdAndDownloadState()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var provider = new StubSourceProvider(
                ("t1", "Song One", "Artist A"),
                ("t2", "Song Two", "Artist B"));
            var store = NewStore(factory, provider);

            var first = await store.AddOrRefreshAsync("stub:playlist");
            var t1Melody = first.Tracks.Single(t => t.ExternalId == "t1").MelodyId;

            await using (var db = await factory.CreateDbContextAsync())
            {
                var track = db.Tracks.Single(t => t.ExternalId == "t1");
                track.DownloadStatus = "downloaded";
                track.CurrentPath = "/music/song1.mp3";
                await db.SaveChangesAsync();
            }

            // Same source, no changes: re-sync must not churn identity.
            await store.AddOrRefreshAsync("stub:playlist");

            await using var db2 = await factory.CreateDbContextAsync();
            var t1 = await db2.Tracks.AsNoTracking().SingleAsync(t => t.ExternalId == "t1");
            Assert.That(t1.MelodyId, Is.EqualTo(t1Melody), "MelodyId must be stable across syncs");
            Assert.That(t1.DownloadStatus, Is.EqualTo("downloaded"), "download state must survive");
            Assert.That(t1.CurrentPath, Is.EqualTo("/music/song1.mp3"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task AutoSyncDue_PlaysOutIntervalLogic()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var provider = new StubSourceProvider(("t1", "Song One", "Artist A"));
            var store = NewStore(factory, provider);

            var added = await store.AddOrRefreshAsync("stub:playlist");

            // Freshly synced: not due even with auto-sync on.
            await store.UpdateSettingsAsync(added.Id, null, autoSync: true, intervalMinutes: 60, targetDirectory: null);
            Assert.That((await store.GetDueForAutoSyncAsync()).Count, Is.EqualTo(0),
                "just synced playlist is not due");

            // Backdate LastSyncAt past the interval: due.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entity = await db.Playlists.SingleAsync(p => p.Id == added.Id);
                entity.LastSyncAt = DateTime.UtcNow.AddMinutes(-90);
                await db.SaveChangesAsync();
            }
            var due = await store.GetDueForAutoSyncAsync();
            Assert.That(due.Select(p => p.Id), Does.Contain(added.Id), "overdue playlist is due");

            // Never synced: due.
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "never-synced",
                    Name = "Never",
                    SourceUrl = "stub:never",
                    AutoSyncEnabled = true,
                    AutoSyncIntervalMinutes = 60,
                });
                await db.SaveChangesAsync();
            }
            Assert.That((await store.GetDueForAutoSyncAsync()).Select(p => p.Id),
                Does.Contain("never-synced"), "never-synced playlist is due");
        }
        finally { TryDelete(dbPath); }
    }



[Test]
public async Task UpdateSettingsAsync_PersistsPreferredFormat()
{
    var dbPath = NewDbPath();
    try
    {
        var factory = await NewDbFactory(dbPath);
        var provider = new StubSourceProvider(("t1", "Song One", "Artist A"));
        var store = NewStore(factory, provider);
        var added = await store.AddOrRefreshAsync("stub:playlist");

        // Default quality.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var fresh = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == added.Id);
            Assert.That(fresh.PreferredFormat, Is.EqualTo("auto"));
        }

        // Valid value round-trips.
        await store.UpdateSettingsAsync(added.Id, null, false, null, null, null, "mp3:192-320");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var fresh = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == added.Id);
            Assert.That(fresh.PreferredFormat, Is.EqualTo("mp3:192-320"));
        }

        // Invalid values fall back to the safe default instead of storing garbage.
        await store.UpdateSettingsAsync(added.Id, null, false, null, null, null, "flac-with-vibes");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var fresh = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == added.Id);
            Assert.That(fresh.PreferredFormat, Is.EqualTo("auto"));
        }
    }
    finally
    {
        TryDelete(dbPath);
    }
}

[Test]
public void ParseQuality_MapsFormatAndCap()
{
    var any = PlaylistStore.ParseQuality(null);
    Assert.That(any.Format, Is.EqualTo(AudioFormat.Auto));
    Assert.That(any.MaxKbps, Is.Null, "auto has no cap");

    var mp3 = PlaylistStore.ParseQuality("mp3");
    Assert.That(mp3.Format, Is.EqualTo(AudioFormat.Mp3));
    Assert.That(mp3.MaxKbps, Is.Null, "a plain format has no cap");

    var capped = PlaylistStore.ParseQuality("mp3:192");
    Assert.That(capped.Format, Is.EqualTo(AudioFormat.Mp3));
    Assert.That(capped.MaxKbps, Is.EqualTo(192));

    // Legacy range strings: the ceiling wins.
    var legacy = PlaylistStore.ParseQuality("mp3:192-320");
    Assert.That(legacy.Format, Is.EqualTo(AudioFormat.Mp3));
    Assert.That(legacy.MaxKbps, Is.EqualTo(320));

    var ceilingOnly = PlaylistStore.ParseQuality("mp3:-320");
    Assert.That(ceilingOnly.MaxKbps, Is.EqualTo(320));

    var floorOnly = PlaylistStore.ParseQuality("flac:900-");
    Assert.That(floorOnly.Format, Is.EqualTo(AudioFormat.Flac));
    Assert.That(floorOnly.MaxKbps, Is.Null);
}

[Test]
public void ParseQuality_PresetsMapToTheirRules()
{
    // Space Saver: anything lossy up to 160 kbps, no floor.
    var saver = PlaylistStore.ParseQuality("preset:saver");
    Assert.That(saver.Format, Is.EqualTo(AudioFormat.Auto),
        "Space Saver accepts any container");
    Assert.That(saver.MaxKbps, Is.EqualTo(160));
    Assert.That(saver.MinKbps, Is.Null);

    // High Quality: anything lossy up to 320 kbps.
    var high = PlaylistStore.ParseQuality("preset:high");
    Assert.That(high.Format, Is.EqualTo(AudioFormat.Auto));
    Assert.That(high.MaxKbps, Is.EqualTo(320),
        "the cap rejects lossless sizes so files stay reasonable");

    // Lossless: FLAC first, best lossy as fallback.
    var (lossless, fallback) = PlaylistStore.ParseQualityDetailed("preset:lossless");
    Assert.That(lossless.Format, Is.EqualTo(AudioFormat.Flac));
    Assert.That(fallback, Is.Not.Null, "Lossless carries a lossy fallback");
    Assert.That(fallback!.MaxKbps, Is.EqualTo(320));

    // Plain auto still means no filter at all.
    var auto = PlaylistStore.ParseQualityDetailed("auto");
    Assert.That(auto.primary.Format, Is.EqualTo(AudioFormat.Auto));
    Assert.That(auto.primary.MaxKbps, Is.Null);
    Assert.That(auto.fallback, Is.Null);
}

[Test]
public void IsValidFormat_AcceptsPresetStrings()
{
    Assert.That(PlaylistStore.IsValidFormat("preset:saver"), Is.True);
    Assert.That(PlaylistStore.IsValidFormat("preset:high"), Is.True);
    Assert.That(PlaylistStore.IsValidFormat("preset:lossless"), Is.True);
    Assert.That(PlaylistStore.IsValidFormat("preset:nope"), Is.False,
        "unknown preset names are rejected like unknown containers");
    Assert.That(PlaylistStore.IsValidFormat("mp3:192-320"), Is.True,
        "the advanced path keeps working");
}

[Test]
public void PresetBands_RejectWhatTheyPromise()
{
    // The waterfall gates measured bitrates; the presets' promises hold.
    var saver = PlaylistStore.ParseQuality("preset:saver");
    Assert.That(saver.IsWithinBand(128), Is.True, "128 kbps fits Space Saver");
    Assert.That(saver.IsWithinBand(320), Is.False, "320 kbps is too big for Space Saver");
    Assert.That(saver.IsWithinBand(900), Is.False,
        "a lossless ~900 kbps file is far outside Space Saver");

    var high = PlaylistStore.ParseQuality("preset:high");
    Assert.That(high.IsWithinBand(320), Is.True, "320 kbps fits High Quality");
    Assert.That(high.IsWithinBand(900), Is.False,
        "FLAC-sized files are what High Quality rejects");
}

[Test]
public void IsValidFormat_RejectsGarbage()
{
    Assert.That(PlaylistStore.IsValidFormat("auto"), Is.True);
    Assert.That(PlaylistStore.IsValidFormat("mp3:192-320"), Is.True);
    Assert.That(PlaylistStore.IsValidFormat("opus"), Is.True);
    Assert.That(PlaylistStore.IsValidFormat("flac-with-vibes"), Is.False);
    Assert.That(PlaylistStore.IsValidFormat("mp3:abc"), Is.False);
    Assert.That(PlaylistStore.IsValidFormat("wav:1-2-3"), Is.False);
}

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
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
