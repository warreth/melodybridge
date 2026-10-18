using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// A refresh rebuilds the snapshot from the source. The measured file
/// facts, the flags and a pending "Download new" decision must survive
/// that rebuild, a partial fetch must keep the rows it did not see, and
/// a playlist that legally contains the same track twice must refresh
/// without throwing. Real SQLite, fresh contexts for every assertion.
/// </summary>
[TestFixture]
public class PlaylistRefreshSnapshotTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-refresh-{test}-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactory(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=False"));
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
            new DownloadManager(new EmptyRegistry(), NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    /// <summary>Mutable stub provider: source changes and partial fetches are simulated.</summary>
    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;

        private readonly List<Track> _tracks;

        /// <summary>Set to simulate a fetch that did not see everything.</summary>
        public string? Warning { get; set; }

        /// <summary>The total the source advertises when Warning is set.</summary>
        public int? DeclaredTrackCount { get; set; }

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

        public bool CanHandle(string sourceIdentifier)
            => sourceIdentifier.Contains("stub:", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
            => Task.FromResult(new Playlist
            {
                Id = "stub-playlist",
                Name = "Stub Playlist",
                Tracks = _tracks.ToList(),
                Warning = Warning,
                TrackCount = DeclaredTrackCount,
            });

    }

    [Test]
    public async Task Refresh_KeepsMeasuredFactsFlagsAndDecisions()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider(("t1", "Song One", "Artist A")));

            var first = await store.AddOrRefreshAsync("stub:playlist");
            var seenAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var track = db.Tracks.Single(t => t.ExternalId == "t1");
                track.DownloadStatus = "downloaded";
                track.CurrentPath = "/music/song1.flac";
                track.Bitrate = 133;
                track.SampleRateHz = 48000;
                track.FileSizeBytes = 3_221_234;
                track.MediaType = "OPUS";
                track.IsHardLink = true;
                track.ArchivePath = "/archive/song1.mp3";
                track.Warning = "low match confidence";
                track.LastSeenAt = seenAt;
                track.SkipDedup = true;
                track.ReviewKind = "length+spectrum";
                track.ReviewText = "duration mismatch: 345.2s vs expected 300s; bitrate looks inflated";
                track.SourcePluginId = "ytdlp";
                track.AvoidPluginId = "archiveorg";
                track.Album = "Kept Album";
                track.AlbumArtist = "Various Artists";
                track.CoverUrl = "https://cdn.example/cover.jpg";
                track.Lyrics = "first line\nsecond line";
                await db.SaveChangesAsync();
            }

            // The source is unchanged: only the snapshot is rebuilt.
            await store.AddOrRefreshAsync("stub:playlist");

            await using (var db = await factory.CreateDbContextAsync())
            {
                var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.ExternalId == "t1");
                Assert.Multiple(() =>
                {
                    Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
                    Assert.That(track.CurrentPath, Is.EqualTo("/music/song1.flac"));
                    Assert.That(track.Bitrate, Is.EqualTo(133),
                        "the measured bitrate is a fact about the file, not source metadata");
                    Assert.That(track.SampleRateHz, Is.EqualTo(48000));
                    Assert.That(track.FileSizeBytes, Is.EqualTo(3_221_234));
                    Assert.That(track.MediaType, Is.EqualTo("OPUS"),
                        "the real container survives; it is not reset to the source default");
                    Assert.That(track.IsHardLink, Is.True);
                    Assert.That(track.ArchivePath, Is.EqualTo("/archive/song1.mp3"));
                    Assert.That(track.Warning, Is.EqualTo("low match confidence"));
                    Assert.That(track.LastSeenAt, Is.EqualTo(seenAt));
                    Assert.That(track.SkipDedup, Is.True,
                        "a pending Download new decision must not be lost in the rebuild");
                    Assert.That(track.ReviewKind, Is.EqualTo("length+spectrum"),
                        "an open Keep or Retry review must survive the rebuild");
                    Assert.That(track.ReviewText, Does.Contain("duration mismatch"));
                    Assert.That(track.SourcePluginId, Is.EqualTo("ytdlp"),
                        "Retry still needs to know who delivered the flagged file");
                    Assert.That(track.AvoidPluginId, Is.EqualTo("archiveorg"),
                        "a retry hint set before the rebuild must still steer that attempt");
                    Assert.That(track.Album, Is.EqualTo("Kept Album"),
                        "a source that temporarily has no album must not blank the stored one");
                    Assert.That(track.AlbumArtist, Is.EqualTo("Various Artists"),
                        "the release credit survives the rebuild");
                    Assert.That(track.CoverUrl, Is.EqualTo("https://cdn.example/cover.jpg"),
                        "the cover URL survives the rebuild");
                    Assert.That(track.Lyrics, Is.EqualTo("first line\nsecond line"),
                        "resolved lyrics survive the rebuild");
                });
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task Refresh_WithTheSameTrackTwice_DoesNotThrow()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider(
                ("dup", "Twice", "Artist"), ("dup", "Twice", "Artist")));

            var first = await store.AddOrRefreshAsync("stub:playlist");
            Assert.That(first.Tracks.Count, Is.EqualTo(2), "both copies are stored on the first save");

            // The old ToDictionary threw here: "an item with the same key".
            await store.AddOrRefreshAsync("stub:playlist");

            await using var db = await factory.CreateDbContextAsync();
            var rows = await db.Tracks.AsNoTracking()
                .Where(t => t.ExternalId == "dup")
                .ToListAsync();
            Assert.That(rows, Has.Count.EqualTo(2),
                "a playlist with the same track twice refreshes without losing a row");
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task PartialFetch_KeepsTheRowsItDidNotSee()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var provider = new StubSourceProvider(
                ("t1", "One", "A"), ("t2", "Two", "B"), ("t3", "Three", "C"));
            var store = NewStore(factory, provider);

            var first = await store.AddOrRefreshAsync("stub:playlist");
            await using (var db = await factory.CreateDbContextAsync())
            {
                var three = db.Tracks.Single(t => t.ExternalId == "t3");
                three.DownloadStatus = "downloaded";
                three.CurrentPath = "/music/three.flac";
                await db.SaveChangesAsync();
            }

            // A rate-limited fetch sees two of the three tracks.
            provider.RemoveTrack("t3");
            provider.Warning = "partial fetch: got 2 of 3 tracks (a page failed after retries)";
            provider.DeclaredTrackCount = 3;

            await store.AddOrRefreshAsync("stub:playlist");

            await using (var db = await factory.CreateDbContextAsync())
            {
                var rows = await db.Tracks.AsNoTracking()
                    .Where(t => t.PlaylistEntityId == first.Id)
                    .ToListAsync();
                Assert.That(rows, Has.Count.EqualTo(3),
                    "a partial fetch must not delete the rows it did not see");

                var three = rows.Single(t => t.ExternalId == "t3");
                Assert.That(three.DownloadStatus, Is.EqualTo("downloaded"));
                Assert.That(three.CurrentPath, Is.EqualTo("/music/three.flac"));

                var playlist = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == first.Id);
                Assert.That(playlist.SyncWarning, Is.Not.Null,
                    "the partial fetch stays visible on the playlist");
                Assert.That(playlist.LastSyncStatus, Is.EqualTo(SyncStatus.CompletedWithWarning));
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task AddingAPlaylist_LinksTracksTheLibraryAlreadyHolds()
    {
        var dbPath = NewDbPath();
        var root = Path.Combine(Path.GetTempPath(), $"mb-addlink-{Guid.NewGuid():N}");
        var musicDir = Path.Combine(root, "music");
        var archiveDir = Path.Combine(root, "archive");
        var dirA = Path.Combine(musicDir, "Playlist A");
        var archiveA = Path.Combine(archiveDir, "Playlist A");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(archiveA);
        try
        {
            var factory = await NewDbFactory(dbPath);
            var settings = new SettingsStore(factory);
            await settings.SetAsync("music_path", musicDir);
            await settings.SetAsync("archive_path", archiveDir);

            // Playlist A already owns the track: a real file in its own
            // folder and its archive copy.
            var fileA = Path.Combine(dirA, "Shared Song.mp3");
            File.WriteAllText(fileA, "audio bytes");
            var archiveFileA = Path.Combine(archiveA, "Shared Song.mp3");
            File.WriteAllText(archiveFileA, "archive bytes");
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-a",
                    Name = "Playlist A",
                    SourceUrl = "https://example.com/a",
                    SourcePlatform = Platform.Spotify,
                    TargetDirectory = dirA,
                    PreferredFormat = "mp3:320",
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "spotify:t1",
                            Title = "Shared Song",
                            Artist = "Artist A",
                            Position = 0,
                            DownloadStatus = "downloaded",
                            CurrentPath = fileA,
                            ArchivePath = archiveFileA,
                            Bitrate = 320,
                            MediaType = "mp3",
                        },
                    },
                });
                await db.SaveChangesAsync();
            }

            var store = NewStore(factory, new StubSourceProvider(("t1", "Shared Song", "Artist A")));
            var playlist = await store.AddOrRefreshAsync("stub:playlist");

            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.Tracks.AsNoTracking()
                    .SingleAsync(t => t.PlaylistEntityId == playlist.Id);
                var folderB = Path.Combine(musicDir, "Stub Playlist");
                Assert.Multiple(() =>
                {
                    Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"),
                        "adding the playlist links the track right away");
                    Assert.That(row.IsHardLink, Is.True, "the row records the shared bytes");
                    Assert.That(row.CurrentPath, Does.StartWith(folderB),
                        "the link lives in this playlist's own folder");
                    Assert.That(row.CurrentPath, Is.EqualTo(
                        Path.Combine(folderB, "Shared Song.mp3")));
                    Assert.That(File.Exists(row.CurrentPath), Is.True);
                    Assert.That(new HardLinkService().LinkCount(fileA), Is.EqualTo(2),
                        "playlist A's file gained a second name, no new bytes");
                    var archiveFolderB = Path.Combine(archiveDir, "Stub Playlist");
                    Assert.That(row.ArchivePath, Is.EqualTo(
                        Path.Combine(archiveFolderB, "Shared Song.mp3")),
                        "the archive copy is linked into this playlist's archive folder too");
                    Assert.That(File.Exists(row.ArchivePath), Is.True);
                    Assert.That(new HardLinkService().LinkCount(archiveFileA), Is.EqualTo(2),
                        "the original archive gained a second name, no new bytes");
                });
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            TryDelete(dbPath);
        }
    }

    [Test]
    public async Task AddingAPlaylist_QualityMismatch_LeavesTheRowForALaterDownload()
    {
        var dbPath = NewDbPath();
        var root = Path.Combine(Path.GetTempPath(), $"mb-addlink-{Guid.NewGuid():N}");
        var musicDir = Path.Combine(root, "music");
        var dirA = Path.Combine(musicDir, "Playlist A");
        Directory.CreateDirectory(dirA);
        try
        {
            var factory = await NewDbFactory(dbPath);
            var settings = new SettingsStore(factory);
            await settings.SetAsync("music_path", musicDir);
            await settings.SetAsync("default_quality", "flac");
            var fileA = Path.Combine(dirA, "Shared Song.mp3");
            File.WriteAllText(fileA, "audio bytes");
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-a",
                    Name = "Playlist A",
                    SourceUrl = "https://example.com/a",
                    SourcePlatform = Platform.Spotify,
                    TargetDirectory = dirA,
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "spotify:t1",
                            Title = "Shared Song",
                            Artist = "Artist A",
                            Position = 0,
                            DownloadStatus = "downloaded",
                            CurrentPath = fileA,
                            Bitrate = 320,
                            MediaType = "mp3",
                        },
                    },
                });
                await db.SaveChangesAsync();
            }

            var store = NewStore(factory, new StubSourceProvider(("t1", "Shared Song", "Artist A")));
            var playlist = await store.AddOrRefreshAsync("stub:playlist");

            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.Tracks.AsNoTracking()
                    .SingleAsync(t => t.PlaylistEntityId == playlist.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(row.DownloadStatus, Is.EqualTo("pending"),
                        "adding a playlist never downloads and never settles a quality question");
                    Assert.That(row.ReviewKind, Is.Null,
                        "no unsolicited review: the user did not ask for a download");
                    Assert.That(row.Warning, Is.Null);
                    Assert.That(new HardLinkService().LinkCount(fileA), Is.EqualTo(1),
                        "the mismatching file stays untouchable");
                });
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            TryDelete(dbPath);
        }
    }

    private static void TryDelete(string dbPath)
    {
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }
}
