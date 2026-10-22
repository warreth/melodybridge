using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Playlist card data through a real store and a real SQLite file:
/// download stats (counts + bytes on disk, downloaded files only) and
/// the saved-playlist lookup the import table marks duplicates with.
/// The stub provider only feeds playlist snapshots; every assertion
/// reads back through a fresh DbContext.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class PlaylistStoreStatsTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-stats-{test}-{Guid.NewGuid():N}.db");

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
        IDbContextFactory<MelodyBridgeDbContext> factory, params ISourceProvider[] providers)
        => NewStore(factory, new EmptyDownloaderRegistry(), providers);

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory,
        IDownloaderRegistry registry, params ISourceProvider[] providers)
        => new(factory, providers,
            new Application.Services.DownloadManager(
                registry, NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;

        public bool CanHandle(string sourceIdentifier)
            => sourceIdentifier.Contains("stub:", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
            => Task.FromResult(new Playlist
            {
                Id = "stub-playlist",
                Name = "Stub Playlist",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "One", Artist = "Stub Artist",
                        SongID = new SongID(Platform.Spotify, "t1"),
                        PlatformSongID = new SongID(Platform.Spotify, "t1"),
                    },
                    new()
                    {
                        Title = "Two", Artist = "Stub Artist",
                        SongID = new SongID(Platform.Spotify, "t2"),
                        PlatformSongID = new SongID(Platform.Spotify, "t2"),
                    },
                },
            });

    }

    [Test]
    public async Task DownloadStats_CountAndBytes_OnlyDownloadedTracks()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, newlySaved) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            Assert.That(newlySaved, Is.True, "first add reports a new playlist");

            // Realistic disk state: one file downloaded with a size, one
            // failed with a partial size, one still pending with none.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var tracks = db.Tracks.ToList();
                tracks.First(t => t.ExternalId == "t1").DownloadStatus = "downloaded";
                tracks.First(t => t.ExternalId == "t1").FileSizeBytes = 10_000_000;
                tracks.First(t => t.ExternalId == "t2").DownloadStatus = "failed";
                tracks.First(t => t.ExternalId == "t2").FileSizeBytes = 123;
                await db.SaveChangesAsync();
            }

            var stats = await store.GetDownloadStatsAsync();
            var (downloaded, failed, total, sizeBytes, attention) = stats[playlist.Id];

            Assert.That(downloaded, Is.EqualTo(1));
            Assert.That(failed, Is.EqualTo(1));
            Assert.That(total, Is.EqualTo(2));
            Assert.That(attention, Is.EqualTo(1), "a failed download needs attention");
            Assert.That(sizeBytes, Is.EqualTo(10_000_000),
                "the failed file's bytes must not count as size on disk");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task DownloadStats_OpenQualityReview_CountsAsAttention()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            await using (var db = await factory.CreateDbContextAsync())
            {
                var track = db.Tracks.First(t => t.ExternalId == "t1");
                track.DownloadStatus = "downloaded";
                track.ReviewKind = "quality";
                await db.SaveChangesAsync();
            }

            var stats = await store.GetDownloadStatsAsync();

            Assert.Multiple(() =>
            {
                Assert.That(stats[playlist.Id].attention, Is.EqualTo(1),
                    "a downloaded file waiting for a review still needs a human");
                Assert.That(stats[playlist.Id].failed, Is.EqualTo(0),
                    "it is not a failed download");
            });
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task DetailedAdd_SecondImport_ReportsUpdateNotNew()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());

            var (first, firstNew) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            var (second, secondNew) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            Assert.That(firstNew, Is.True);
            Assert.That(secondNew, Is.False,
                "the second add must say it updated, not saved - the add form says so");
            Assert.That(second.Id, Is.EqualTo(first.Id),
                "the same playlist row is reused, not duplicated");

            var saved = await store.GetSavedExternalIdsAsync(Platform.Spotify);
            Assert.That(saved.Contains("stub-playlist"), Is.True);
            Assert.That(saved.Count, Is.EqualTo(1), "one playlist in, one external id known");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SavedExternalIds_ArePerPlatform()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            await store.AddOrRefreshAsync("stub:playlist");

            var spotifyIds = await store.GetSavedExternalIdsAsync(Platform.Spotify);
            var youtubeIds = await store.GetSavedExternalIdsAsync(Platform.YouTubeMusic);

            Assert.That(spotifyIds.Contains("stub-playlist"), Is.True);
            Assert.That(youtubeIds.Count, Is.EqualTo(0),
                "a Spotify playlist must not mark a YouTube import as saved");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task UpdateScheduleAsync_StoresTheCronAndDropsLegacyColumns()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.FromCron("0 * * * *"));

            await using var db = factory.CreateDbContext();
            var saved = await db.Playlists.FindAsync(playlist.Id);
            Assert.That(saved!.ScheduleCron, Is.EqualTo("0 * * * *"),
                "the schedule string is the single source of truth");
            Assert.That(saved.AutoSyncEnabled, Is.True, "legacy readers still see the boolean");
            Assert.That(saved.AutoSyncIntervalMinutes, Is.Null,
                "a cron schedule carries no minutes column value");

            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual);
            await using var db2 = factory.CreateDbContext();
            var manual = await db2.Playlists.FindAsync(playlist.Id);
            Assert.That(manual!.ScheduleCron, Is.EqualTo(""));
            Assert.That(manual.AutoSyncEnabled, Is.False);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task UpdateScheduleAsync_EmptyFolder_ClearsTheOverride()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: "/tmp/mb-clear-target", archiveDirectory: "/tmp/mb-clear-archive");
            await using (var db = factory.CreateDbContext())
            {
                var stored = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(stored.TargetDirectory, Is.EqualTo("/tmp/mb-clear-target"));
                Assert.That(stored.ArchiveDirectory, Is.EqualTo("/tmp/mb-clear-archive"));
            }

            // Empty strings clear the overrides; the UI sends them when the
            // user empties a folder field.
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: "", archiveDirectory: "");
            await using (var db = factory.CreateDbContext())
            {
                var cleared = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(cleared.TargetDirectory, Is.Null,
                    "clearing the field removes the folder, it does not silently keep the old value");
                Assert.That(cleared.ArchiveDirectory, Is.Null);
            }

            // Null still means "leave the stored value alone".
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: "/tmp/mb-kept", archiveDirectory: null);
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.FromCron("0 4 * * *"),
                targetDirectory: null);
            await using (var db = factory.CreateDbContext())
            {
                var kept = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(kept.TargetDirectory, Is.EqualTo("/tmp/mb-kept"),
                    "a null parameter never wipes a stored folder");
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task UpdateScheduleAsync_PersistsTheAutoDownloadToggle()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            await using (var db = factory.CreateDbContext())
            {
                var fresh = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(fresh.AutoDownloadEnabled, Is.False,
                    "off by default: downloading stays a deliberate choice");
            }

            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual, autoDownload: true);
            await using (var db = factory.CreateDbContext())
            {
                var on = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(on.AutoDownloadEnabled, Is.True, "the toggle persists");
            }

            // A save that only touches the schedule must not reset the flag.
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.FromCron("0 * * * *"));
            await using (var db = factory.CreateDbContext())
            {
                var kept = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(kept.AutoDownloadEnabled, Is.True,
                    "null leaves the stored value alone");
            }

            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual, autoDownload: false);
            await using (var db = factory.CreateDbContext())
            {
                var off = await db.Playlists.AsNoTracking().SingleAsync(p => p.Id == playlist.Id);
                Assert.That(off.AutoDownloadEnabled, Is.False, "the toggle turns back off");
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SetPinned_PersistsAcrossAStoreReload()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            await using (var db = factory.CreateDbContext())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pin-1",
                    Name = "Pin me",
                    SourceUrl = "stub:pin-1",
                });
                await db.SaveChangesAsync();
            }
            var store = NewStore(factory);

            await store.SetPinnedAsync("pin-1", true);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Playlists.AsNoTracking().SingleAsync(p => p.Id == "pin-1");
                Assert.That(row.IsPinned, Is.True, "the pin is stored on the playlist row");
            }
            await store.SetPinnedAsync("pin-1", false);
            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Playlists.AsNoTracking().SingleAsync(p => p.Id == "pin-1");
                Assert.That(row.IsPinned, Is.False, "unpin clears the same row");
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldPlaylists_GainThePinnedColumn()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            await using (var db = factory.CreateDbContext())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "old-pin",
                    Name = "Old pin",
                    SourceUrl = "stub:old-pin",
                });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Playlists DROP COLUMN IsPinned");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Playlists.AsNoTracking().SingleAsync(p => p.Id == "old-pin");
                Assert.Multiple(() =>
                {
                    Assert.That(row.IsPinned, Is.False, "existing rows stay unpinned");
                    Assert.That(row.Name, Is.EqualTo("Old pin"), "data survives the patch");
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldPlaylists_GainTheAutoDownloadColumn()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "old-pl",
                    Name = "Old",
                    SourceUrl = "stub:old",
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Playlists DROP COLUMN AutoDownloadEnabled");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Playlists.AsNoTracking().SingleAsync(p => p.Id == "old-pl");
                Assert.Multiple(() =>
                {
                    Assert.That(row.AutoDownloadEnabled, Is.False, "existing rows keep the off default");
                    Assert.That(row.Name, Is.EqualTo("Old"), "data survives the patch");
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldSyncJobs_GainTheLastRunCounts()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.SyncJobs.Add(new SyncJobEntity
                {
                    Id = "old-job",
                    Name = "Old job",
                    Schedule = "Manual",
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE SyncJobs DROP COLUMN LastRunResolvedTracks");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE SyncJobs DROP COLUMN LastRunTotalTracks");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "old-job");
                Assert.Multiple(() =>
                {
                    Assert.That(row.LastRunResolvedTracks, Is.EqualTo(0), "existing rows default to zero");
                    Assert.That(row.LastRunTotalTracks, Is.EqualTo(0));
                    Assert.That(row.Name, Is.EqualTo("Old job"), "data survives the patch");
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldSyncJobs_GainTheCreatedAtColumn()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.SyncJobs.Add(new SyncJobEntity
                {
                    Id = "old-job-anchor",
                    Name = "Old job without an anchor",
                    Schedule = "Daily",
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE SyncJobs DROP COLUMN CreatedAt");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "old-job-anchor");
                Assert.Multiple(() =>
                {
                    Assert.That(row.Name, Is.EqualTo("Old job without an anchor"), "data survives the patch");
                    Assert.That(row.CreatedAt, Is.Null,
                        "a row from before the column has no anchor; the scheduler treats it as due at once");
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldPlaylists_GainTheAttemptStamp()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "old-playlist-attempt",
                    Name = "Old playlist",
                    SourceUrl = "stub:old",
                    LastSyncAt = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc),
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Playlists DROP COLUMN LastAttemptAt");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Playlists.AsNoTracking()
                    .SingleAsync(p => p.Id == "old-playlist-attempt");
                Assert.Multiple(() =>
                {
                    Assert.That(row.LastSyncAt, Is.Not.Null, "data survives the patch");
                    Assert.That(row.LastAttemptAt, Is.Null,
                        "an old row was never attempted; the next due check starts it");
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldSyncJobs_GainTheTargetsColumn()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.SyncJobs.Add(new SyncJobEntity
                {
                    Id = "old-job-targets",
                    Name = "Old single-target job",
                    Schedule = "Manual",
                    OutputTarget = "M3uFile",
                    M3uOutputPath = "/out/old.m3u",
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE SyncJobs DROP COLUMN TargetsJson");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "old-job-targets");
                var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(row);
                Assert.Multiple(() =>
                {
                    Assert.That(row.Name, Is.EqualTo("Old single-target job"), "data survives the patch");
                    Assert.That(row.TargetsJson, Is.Null, "the old row keeps its legacy fields");
                    Assert.That(targets.Single().Kind, Is.EqualTo(MelodyBridge.Core.OutputTargetType.M3uFile));
                    Assert.That(targets.Single().M3uPath, Is.EqualTo("/out/old.m3u"));
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldPlaylists_GainTheStrictQualityColumns()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "old-pl",
                    Name = "Old",
                    SourceUrl = "stub:old",
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema.
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Playlists DROP COLUMN StrictQuality");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Playlists DROP COLUMN AllowContainerChange");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Playlists.AsNoTracking().SingleAsync(p => p.Id == "old-pl");
                Assert.Multiple(() =>
                {
                    Assert.That(row.StrictQuality, Is.True,
                        "existing playlists stay strict: the old automatic lossy fallback is now opt-in");
                    Assert.That(row.AllowContainerChange, Is.True,
                        "container changes stay allowed once strict is turned off");
                    Assert.That(row.Name, Is.EqualTo("Old"), "data survives the patch");
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SchemaPatcher_OldTracks_GainTheQualityReviewColumns()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);

            await using (var db = factory.CreateDbContext())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "old-pl-review",
                    Name = "Old reviews",
                    SourceUrl = "stub:old-review",
                    Tracks = new List<TrackEntity>
                    {
                        new() { MelodyId = "old-m-review", Title = "Old Song", Artist = "Artist" },
                    },
                });
                await db.SaveChangesAsync();

                // Simulate the pre-column schema. An old release had no
                // counter index, and SQLite refuses to drop a column an
                // index depends on.
                await db.Database.ExecuteSqlRawAsync(
                    "DROP INDEX IF EXISTS \"IX_Tracks_PlaylistEntityId_DownloadStatus_FileSizeBytes_ReviewKind\"");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks DROP COLUMN SourcePluginId");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks DROP COLUMN ReviewKind");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks DROP COLUMN ReviewText");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks DROP COLUMN AvoidPluginId");
            }

            await using (var db = factory.CreateDbContext())
                await SchemaPatcher.PatchAsync(db);

            await using (var check = factory.CreateDbContext())
            {
                var row = await check.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "old-m-review");
                Assert.Multiple(() =>
                {
                    Assert.That(row.Title, Is.EqualTo("Old Song"), "data survives the patch");
                    Assert.That(row.ReviewKind, Is.Null, "existing tracks start without an open review");
                    Assert.That(row.ReviewText, Is.Null);
                    Assert.That(row.SourcePluginId, Is.Null);
                    Assert.That(row.AvoidPluginId, Is.Null);
                });
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task DueForAutoSync_FollowsTheCronSchedule()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            // Daily at 03:00, synced "now": not due within the next hours.
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.FromCron("0 3 * * *"));
            await using (var db = factory.CreateDbContext())
            {
                var row = await db.Playlists.FindAsync(playlist.Id);
                row!.LastSyncAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            var dueRightAfter = await store.GetDueForAutoSyncAsync();
            Assert.That(dueRightAfter.Select(p => p.Id), Does.Not.Contain(playlist.Id),
                "freshly synced on a daily schedule, so nothing is due yet");

            // Push last sync past yesterday's 03:00: due again.
            await using (var db = factory.CreateDbContext())
            {
                var row = await db.Playlists.FindAsync(playlist.Id);
                row!.LastSyncAt = DateTime.UtcNow.AddHours(-25);
                await db.SaveChangesAsync();
            }

            var dueNow = await store.GetDueForAutoSyncAsync();
            Assert.That(dueNow.Select(p => p.Id), Does.Contain(playlist.Id),
                "a daily schedule whose last sync was 25 hours ago is due");

            // Manual is never due.
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual);
            var dueManual = await store.GetDueForAutoSyncAsync();
            Assert.That(dueManual.Select(p => p.Id), Does.Not.Contain(playlist.Id));
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task Patcher_ConvertsLegacyAutoSyncRowsOnce()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            // An older build's row: boolean + minutes, no schedule string.
            await using (var db = factory.CreateDbContext())
            {
                var row = await db.Playlists.FindAsync(playlist.Id);
                row!.AutoSyncEnabled = true;
                row.AutoSyncIntervalMinutes = 45;
                await db.SaveChangesAsync();
            }

            await using (var db = factory.CreateDbContext())
            {
                await SchemaPatcher.PatchAsync(db);
            }

            await using var check = factory.CreateDbContext();
            var converted = await check.Playlists.FindAsync(playlist.Id);
            Assert.That(converted!.ScheduleCron, Is.EqualTo("*/45 * * * *"),
                "the legacy minutes become the equivalent cron expression");

            // Idempotent: a second pass leaves the schedule alone.
            converted.Name = "renamed";
            await check.SaveChangesAsync();
            await using (var db = factory.CreateDbContext())
            {
                await SchemaPatcher.PatchAsync(db);
            }
            await using var again = factory.CreateDbContext();
            var reread = await again.Playlists.FindAsync(playlist.Id);
            Assert.That(reread!.ScheduleCron, Is.EqualTo("*/45 * * * *"));
            Assert.That(reread.Name, Is.EqualTo("renamed"),
                "the patcher never touches a row that already has a schedule");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    /// <summary>Serves a real file, but fails when asked for a format it cannot produce.</summary>
    private sealed class FormatGateDownloader : IDownloader
    {
        private readonly string _file;
        public FormatGateDownloader(string file) => _file = file;
        public string Id => "gate";
        public string Name => "Format Gate";
        public string Description => string.Empty;
        // Serves real MP3 files only; the manifest says exactly that.
        public PluginCapabilities Capabilities { get; } =
            PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(query.Title, query.Artist, "https://gate.example/1", null));
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            // Like a lossless-only source without the requested manifest:
            // the requested format is simply not available.
            if (quality?.Format == AudioFormat.Flac)
                return Task.FromResult(new DownloaderDownloadResult(false, null, "lossless not available"));
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"gate-{Path.GetFileName(_file)}");
            File.Copy(_file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    [Test]
    public async Task LosslessPreset_Strict_FailsWhenNoLosslessSourceExists()
    {
        var dbPath = NewDbPath();
        var downloadDir = Path.Combine(Path.GetTempPath(), $"mb-dl-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var registry = new ListDownloaderRegistry(new FormatGateDownloader(MakeRealMp3(192)));
            var store = NewStore(factory, registry, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: downloadDir, preferredFormat: "preset:lossless");

            var (downloaded, failed) = await store.DownloadMissingAsync(playlist.Id, limit: 5);

            Assert.That(downloaded, Is.EqualTo(0),
                "strict is the default: only real lossless content satisfies the request");
            Assert.That(failed, Is.EqualTo(2));

            await using var db = factory.CreateDbContext();
            var track = db.Tracks.First(t => t.PlaylistEntityId == playlist.Id);
            Assert.That(track.DownloadStatus, Is.EqualTo("failed"));
            Assert.That(track.DownloadError, Does.Contain("excluded every plugin"),
                "the row names the quality gate, not a vague plugin failure");
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(downloadDir))
                try { Directory.Delete(downloadDir, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task LosslessPreset_NonStrict_StepsDownToTheBestLossyFile()
    {
        var dbPath = NewDbPath();
        var downloadDir = Path.Combine(Path.GetTempPath(), $"mb-dl-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var registry = new ListDownloaderRegistry(new FormatGateDownloader(MakeRealMp3(192)));
            var store = NewStore(factory, registry, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: downloadDir, preferredFormat: "preset:lossless",
                strictQuality: false, allowContainerChange: true);

            var (downloaded, failed) = await store.DownloadMissingAsync(playlist.Id, limit: 5);

            Assert.That(downloaded, Is.EqualTo(2),
                "non-strict: the ladder steps down to the lossy file the plugin declares");
            Assert.That(failed, Is.EqualTo(0));

            await using var db = factory.CreateDbContext();
            var track = db.Tracks.First(t => t.PlaylistEntityId == playlist.Id);
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(Path.GetExtension(track.CurrentPath), Is.EqualTo(".mp3"),
                "the fallback lands on the lossy container the manifest declares");
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(downloadDir))
                try { Directory.Delete(downloadDir, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SingleTrackDownload_PassesThePlaylistQualityPolicy()
    {
        var dbPath = NewDbPath();
        var downloadDir = Path.Combine(Path.GetTempPath(), $"mb-dl-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var registry = new ListDownloaderRegistry(new FormatGateDownloader(MakeRealMp3(192)));
            var store = NewStore(factory, registry, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: downloadDir, preferredFormat: "preset:lossless",
                strictQuality: false, allowContainerChange: true);

            int trackId;
            await using (var db = factory.CreateDbContext())
                trackId = db.Tracks.Where(t => t.PlaylistEntityId == playlist.Id)
                    .OrderBy(t => t.Position).First().Id;

            var result = await store.DownloadTrackAsync(trackId);

            Assert.That(result, Is.EqualTo("downloaded"),
                "the per-track button reads the playlist's strict setting too");
            await using (var db = factory.CreateDbContext())
            {
                var track = db.Tracks.Single(t => t.Id == trackId);
                Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
                Assert.That(Path.GetExtension(track.CurrentPath), Is.EqualTo(".mp3"),
                    "it landed on the lossy level the manifest declares");
            }
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(downloadDir))
                try { Directory.Delete(downloadDir, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task StrictFilter_FailureNamesTheFiltersOnTheTrack()
    {
        var dbPath = NewDbPath();
        var downloadDir = Path.Combine(Path.GetTempPath(), $"mb-dl-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            // Serves a real 320 kbps file, but the playlist caps at 160.
            var registry = new ListDownloaderRegistry(new FormatGateDownloader(MakeRealMp3(320)));
            var store = NewStore(factory, registry, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            await store.UpdateScheduleAsync(playlist.Id, null, ScanSchedule.Manual,
                targetDirectory: downloadDir, preferredFormat: "preset:saver");

            var (downloaded, failed) = await store.DownloadMissingAsync(playlist.Id, limit: 5);

            Assert.That(downloaded, Is.EqualTo(0));
            Assert.That(failed, Is.EqualTo(2),
                "both stub tracks only exist above the 160 kbps cap");

            await using var db = factory.CreateDbContext();
            var track = db.Tracks.First(t => t.PlaylistEntityId == playlist.Id);
            Assert.That(track.DownloadStatus, Is.EqualTo("failed"));
            Assert.That(track.DownloadError, Does.Contain("outside your quality filters"),
                "the track row says the filters rejected it, not a vague plugin failure");
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(downloadDir))
                try { Directory.Delete(downloadDir, true); } catch { /* best effort */ }
        }
    }

    private static string MakeRealMp3(int kbps)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"src-{kbps}.mp3");
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is { } probe
            ? Path.Combine(Path.GetDirectoryName(probe)!, "ffmpeg")
            : "ffmpeg";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -c:a libmp3lame -b:a {kbps}k \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        return path;
    }

    [Test]
    public async Task LegacyIntervalRows_StayDueUntilThePatcherConvertsThem()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, _) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            // A row saved by an older build: boolean + minutes, no schedule string.
            await using (var db = factory.CreateDbContext())
            {
                var row = await db.Playlists.FindAsync(playlist.Id);
                row!.AutoSyncEnabled = true;
                row.AutoSyncIntervalMinutes = 30;
                row.LastSyncAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            var fresh = await store.GetDueForAutoSyncAsync();
            Assert.That(fresh.Select(p => p.Id), Does.Not.Contain(playlist.Id),
                "synced 0 of 30 minutes ago: not due");

            await using (var db = factory.CreateDbContext())
            {
                var row = await db.Playlists.FindAsync(playlist.Id);
                row!.LastSyncAt = DateTime.UtcNow.AddMinutes(-31);
                await db.SaveChangesAsync();
            }

            var due = await store.GetDueForAutoSyncAsync();
            Assert.That(due.Select(p => p.Id), Does.Contain(playlist.Id),
                "legacy interval rows keep syncing on their old cadence");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }
}
