using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The download folder layout: every playlist gets its own folder under
/// the default music path (folder named after the playlist, liked songs
/// included), an explicit TargetDirectory is used as it is, the archive
/// copy mirrors the layout under the archive path, and a playlist can
/// switch its archive off. Real SQLite, real PlaylistStore, plugin that
/// writes real files.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DefaultDownloadPathTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-default-{test}-{Guid.NewGuid():N}.db");

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

    /// <summary>Writes the same tiny valid MP3 the other store tests use.</summary>
    private sealed class FileWritingDownloader : IDownloader
    {
        public string Id => "file-writer";
        public string Name => "File Writer (test)";
        public List<string> OutputDirectories = new();

        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, $"https://example.com/{query.Artist}-{query.Title}", TimeSpan.FromSeconds(180)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            OutputDirectories.Add(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");

            var id3Header = new byte[]
            {
                0x49, 0x44, 0x33, 0x03, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };
            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
            var fileBytes = new byte[id3Header.Length + frame.Length * 40];
            id3Header.CopyTo(fileBytes, 0);
            for (var i = 0; i < 40; i++)
                frame.CopyTo(fileBytes, id3Header.Length + frame.Length * i);
            await System.IO.File.WriteAllBytesAsync(path, fileBytes, ct);

            return new DownloaderDownloadResult(true, path, null);
        }
    }

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory,
        params IDownloader[] downloaders)
        => NewStore(factory, null, downloaders);

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory,
        IAccountSourceProvider? account,
        params IDownloader[] downloaders)
        => new(factory,
            new ISourceProvider[] { new StubSourceProvider() },
            new Application.Services.DownloadManager(
                new StubRegistry(downloaders),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            account is null ? null : new[] { account });

    /// <summary>A connected account whose liked songs can be imported.</summary>
    private sealed class StubAccountProvider : IAccountSourceProvider
    {
        public string Name => "Spotify";
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string> BeginLoginAsync(string redirectUrl, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> CompleteLoginAsync(string redirectQuery, string redirectUrl, CancellationToken ct = default) => Task.FromResult("");
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveSettingAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserPlaylist>>(Array.Empty<UserPlaylist>());

        public Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
            => Task.FromResult(new Playlist
            {
                Id = "spotify-liked",
                Name = "Liked songs (Spotify)",
                SourceUrl = "spotify:liked",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "Liked Song",
                        Artist = "Artist L",
                        IsLiked = true,
                        SongID = new SongID(Platform.Spotify, "liked-1"),
                        PlatformSongID = new SongID(Platform.Spotify, "liked-1"),
                    },
                },
            });
    }

    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;
        public bool CanHandle(string sourceIdentifier) => sourceIdentifier.Contains("stub:");
        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier) => throw new NotImplementedException();
    }

    /// <summary>A downloader whose every call is a bug on the dedup path.</summary>
    private sealed class ThrowingDownloader : IDownloader
    {
        public string Id => "throwing";
        public string Name => "Should not be called";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => throw new AssertionException("dedup must not search when a link is possible");
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
            => throw new AssertionException("dedup must not download when a link is possible");
    }

    private sealed class StubRegistry : IDownloaderRegistry
    {
        private readonly IDownloader[] _downloaders;
        public StubRegistry(IDownloader[] downloaders) => _downloaders = downloaders;
        public IReadOnlyList<IDownloader> GetAll() => _downloaders;
        public IDownloader? Get(string id) => _downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => _downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
        public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { System.IO.File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }

    /// <summary>Seeds a playlist whose TargetDirectory is deliberately null.</summary>
    private static async Task SeedPlaylistWithoutFolderAsync(IDbContextFactory<MelodyBridgeDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-default",
            Name = "Default Path",
            SourceUrl = "stub:pl",
            SyncMode = "Additive",
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "mel-d1",
                    Title = "Default Path Song",
                    Artist = "Artist A",
                    DownloadStatus = "pending",
                    Position = 0,
                },
            },
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task DownloadMissing_NoPlaylistFolder_LandsInItsOwnFolderUnderMusicPath()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-music-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var plugin = new FileWritingDownloader();
            var store = NewStore(factory, plugin);
            await new SettingsStore(factory).SetAsync("music_path", musicDir);
            await SeedPlaylistWithoutFolderAsync(factory);

            var (downloaded, failed) = await store.DownloadMissingAsync("pl-default");

            var expectedDir = Path.Combine(musicDir, "Default Path");
            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            Assert.That(plugin.OutputDirectories, Has.All.EqualTo(expectedDir),
                "the playlist gets a folder named after itself under the music_path setting");

            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-d1");
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(Path.GetDirectoryName(track.CurrentPath), Is.EqualTo(expectedDir),
                "the file must land inside the playlist's own folder");
            Assert.That(System.IO.File.Exists(track.CurrentPath), Is.True);
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DownloadMissing_TwoPlaylists_SameTrack_EachOwnFolder_SecondHardlinks()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-multi-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await new SettingsStore(factory).SetAsync("music_path", musicDir);
            await SeedSharedTrackPlaylistsAsync(factory);

            // Playlist one really downloads the file.
            var firstPlugin = new FileWritingDownloader();
            var firstStore = NewStore(factory, firstPlugin);
            var first = await firstStore.DownloadMissingAsync("pl-road");
            var dirRoad = Path.Combine(musicDir, "Road Trip");
            Assert.That(first, Is.EqualTo((1, 0)));
            Assert.That(firstPlugin.OutputDirectories, Has.All.EqualTo(dirRoad));

            // Measure the row like a real download would, so dedup can
            // compare the quality.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.Tracks.SingleAsync(t => t.PlaylistEntityId == "pl-road");
                row.MediaType = "mp3";
                row.Bitrate = 320;
                await db.SaveChangesAsync();
            }

            // Playlist two owns the same track: the bytes are linked into
            // its own folder, the downloader is never called.
            var secondStore = NewStore(factory, new ThrowingDownloader());
            var second = await secondStore.DownloadMissingAsync("pl-chill");

            Assert.That(second, Is.EqualTo((1, 0)));
            await using var check = await factory.CreateDbContextAsync();
            var road = await check.Tracks.AsNoTracking().SingleAsync(t => t.PlaylistEntityId == "pl-road");
            var chill = await check.Tracks.AsNoTracking().SingleAsync(t => t.PlaylistEntityId == "pl-chill");
            var dirChill = Path.Combine(musicDir, "Chill");
            Assert.That(Path.GetDirectoryName(road.CurrentPath), Is.EqualTo(dirRoad));
            Assert.That(Path.GetDirectoryName(chill.CurrentPath), Is.EqualTo(dirChill),
                "the hardlink lands in the second playlist's own folder");
            Assert.That(chill.IsHardLink, Is.True);
            Assert.That(chill.CurrentPath, Is.Not.EqualTo(road.CurrentPath));
            Assert.That(new MelodyBridge.Infrastructure.Files.HardLinkService().LinkCount(road.CurrentPath!),
                Is.EqualTo(2), "one inode, one name per playlist folder");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Archive_GlobalPath_KeepsThePlaylistFolderLayout()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-arc-music-{Guid.NewGuid():N}");
        var archiveDir = Path.Combine(Path.GetTempPath(), $"mb-default-arc-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var settings = new SettingsStore(factory);
            await settings.SetAsync("music_path", musicDir);
            await settings.SetAsync("archive_path", archiveDir);
            await SeedPlaylistWithoutFolderAsync(factory);

            var (downloaded, failed) = await NewStore(factory, new FileWritingDownloader())
                .DownloadMissingAsync("pl-default");

            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-d1");
            var expectedArchive = Path.Combine(archiveDir, "Default Path", "mel-d1.mp3");
            Assert.That(track.ArchivePath, Is.EqualTo(expectedArchive),
                "the archive copy mirrors the playlist folder under the archive path");
            Assert.That(System.IO.File.Exists(expectedArchive), Is.True);
            Assert.That(Path.GetDirectoryName(track.CurrentPath),
                Is.EqualTo(Path.Combine(musicDir, "Default Path")));
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(archiveDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Archive_GlobalFormat_ConvertsTheCopy()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-fmt-music-{Guid.NewGuid():N}");
        var archiveDir = Path.Combine(Path.GetTempPath(), $"mb-default-fmt-arc-{Guid.NewGuid():N}");
        try
        {
            if (MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg") is null)
                Assert.Ignore("ffmpeg is not installed on this machine");

            var factory = await NewDbFactory(dbPath);
            var settings = new SettingsStore(factory);
            await settings.SetAsync("music_path", musicDir);
            await settings.SetAsync("archive_path", archiveDir);
            await settings.SetAsync("archive_format", "flac");
            await SeedPlaylistWithoutFolderAsync(factory);

            var (downloaded, failed) = await NewStore(factory, new FileWritingDownloader())
                .DownloadMissingAsync("pl-default");

            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-d1");
            var expectedArchive = Path.Combine(archiveDir, "Default Path", "mel-d1.flac");
            Assert.That(track.ArchivePath, Is.EqualTo(expectedArchive),
                "the app-wide archive format default reaches the copier, extension included");
            Assert.That(System.IO.File.Exists(expectedArchive), Is.True,
                "the transcoded copy really exists");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(archiveDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Archive_SwitchedOff_MakesNoCopyEvenWithGlobalPath()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-off-music-{Guid.NewGuid():N}");
        var archiveDir = Path.Combine(Path.GetTempPath(), $"mb-default-off-arc-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var settings = new SettingsStore(factory);
            await settings.SetAsync("music_path", musicDir);
            await settings.SetAsync("archive_path", archiveDir);
            await SeedPlaylistWithoutFolderAsync(factory);
            await using (var seedDb = await factory.CreateDbContextAsync())
            {
                (await seedDb.Playlists.SingleAsync(p => p.Id == "pl-default")).ArchiveEnabled = false;
                await seedDb.SaveChangesAsync();
            }

            var (downloaded, failed) = await NewStore(factory, new FileWritingDownloader())
                .DownloadMissingAsync("pl-default");

            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-d1");
            Assert.That(track.ArchivePath, Is.Null,
                "a playlist with the archive off gets no copy despite the global archive path");
            Assert.That(Directory.Exists(archiveDir), Is.False, "not even the folder is created");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(archiveDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task LikedSongsImport_DownloadsIntoItsOwnFolder()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-liked-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await new SettingsStore(factory).SetAsync("music_path", musicDir);
            var plugin = new FileWritingDownloader();
            var store = NewStore(factory, new StubAccountProvider(), plugin);

            var liked = await store.AddOrRefreshLikedAsync("Spotify");
            var (downloaded, failed) = await store.DownloadMissingAsync(liked.Id);

            var expectedDir = Path.Combine(musicDir, "Liked songs (Spotify)");
            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            Assert.That(plugin.OutputDirectories, Has.All.EqualTo(expectedDir),
                "liked songs get their own folder like any playlist");

            await using var db = await factory.CreateDbContextAsync();
            var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.PlaylistEntityId == liked.Id);
            Assert.That(row.IsLiked, Is.True);
            Assert.That(Path.GetDirectoryName(row.CurrentPath), Is.EqualTo(expectedDir));
            Assert.That(System.IO.File.Exists(row.CurrentPath), Is.True);
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task NewPlaylist_StoresTheDefaultQualityFromSettings()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-q-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var settings = new SettingsStore(factory);
            await settings.SetAsync("music_path", musicDir);
            await settings.SetAsync("default_quality", "preset:high");
            var store = NewStore(factory, new StubAccountProvider(), new FileWritingDownloader());

            // The account/liked import path does not pass a quality: the
            // app-wide default must seed the new playlist anyway.
            var liked = await store.AddOrRefreshLikedAsync("Spotify");

            Assert.That(liked.PreferredFormat, Is.EqualTo("preset:high"),
                "a new playlist starts with the default quality from Settings");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Test]
    public async Task RemoveTrack_DeleteChoice_RemovesPrimaryAndArchiveCopy()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-rm-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var (trackId, primary, archive) = await SeedTrackWithArchiveAsync(factory, musicDir);
            var store = NewStore(factory);

            var removed = await store.RemoveTrackAsync("pl-rm", trackId, deleteFile: true);

            Assert.That(removed, Is.True);
            Assert.That(File.Exists(primary), Is.False, "the delete choice removes the download");
            Assert.That(File.Exists(archive), Is.False, "and its archive copy with it");
            await using var db = await factory.CreateDbContextAsync();
            Assert.That(await db.Tracks.CountAsync(), Is.EqualTo(0));
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RemoveTrack_KeepChoice_LeavesBothFiles()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-rmk-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var (trackId, primary, archive) = await SeedTrackWithArchiveAsync(factory, musicDir);
            var store = NewStore(factory);

            var removed = await store.RemoveTrackAsync("pl-rm", trackId, deleteFile: false);

            Assert.That(removed, Is.True);
            Assert.That(File.Exists(primary), Is.True, "keeping means the music stays where it is");
            Assert.That(File.Exists(archive), Is.True);
            await using var db = await factory.CreateDbContextAsync();
            Assert.That(await db.Tracks.CountAsync(), Is.EqualTo(0), "only the row leaves");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DeleteTrackFiles_KeepsTheRowAndResetsItToPending()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-rst-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var (trackId, primary, archive) = await SeedTrackWithArchiveAsync(factory, musicDir);
            var store = NewStore(factory);

            var deleted = await store.DeleteTrackFilesAsync("pl-rm", trackId);

            Assert.That(deleted.Deleted, Is.True);
            Assert.That(deleted.Error, Is.Null);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(primary), Is.False, "the file goes");
                Assert.That(File.Exists(archive), Is.False, "and its archive copy with it");
            });
            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.AsNoTracking().SingleAsync();
            Assert.Multiple(() =>
            {
                Assert.That(track.DownloadStatus, Is.EqualTo("pending"),
                    "the track stays and asks to be downloaded again");
                Assert.That(track.CurrentPath, Is.Null);
                Assert.That(track.ArchivePath, Is.Null);
                Assert.That(track.MelodyId, Is.EqualTo("mel-rm"), "the row keeps its identity");
            });
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DeleteTrackFiles_WhenTheFileCannotBeDeleted_LeavesTheRowAlone()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("needs POSIX directory permissions");

        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-rmfail-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var (trackId, primary, _) = await SeedTrackWithArchiveAsync(factory, musicDir);
            var store = NewStore(factory);
            var dir = Path.GetDirectoryName(primary)!;

            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var outcome = await store.DeleteTrackFilesAsync("pl-rm", trackId);

                Assert.Multiple(() =>
                {
                    Assert.That(outcome.Deleted, Is.False);
                    Assert.That(outcome.Error, Does.Contain("could not delete"),
                        "the failure is reported, not swallowed");
                });
                await using var db = await factory.CreateDbContextAsync();
                var track = await db.Tracks.AsNoTracking().SingleAsync();
                Assert.Multiple(() =>
                {
                    Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"),
                        "resetting the row would let the next scan re-adopt the file");
                    Assert.That(track.CurrentPath, Is.EqualTo(primary));
                    Assert.That(File.Exists(primary), Is.True);
                });
            }
            finally
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DeleteTrackFiles_WhenTheArchiveCannotBeDeleted_KeepsThePrimaryAndTheRow()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("needs POSIX directory permissions");

        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-rmarch-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var (trackId, primary, archive) = await SeedTrackWithArchiveAsync(factory, musicDir);
            var store = NewStore(factory);
            var archiveDir = Path.GetDirectoryName(archive)!;

            File.SetUnixFileMode(archiveDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var outcome = await store.DeleteTrackFilesAsync("pl-rm", trackId);

                Assert.Multiple(() =>
                {
                    Assert.That(outcome.Deleted, Is.False);
                    Assert.That(outcome.Error, Does.Contain("could not delete"));
                    Assert.That(File.Exists(primary), Is.True,
                        "the primary is attempted last, so a failed archive leaves it untouched");
                    Assert.That(File.Exists(archive), Is.True);
                });
                await using var db = await factory.CreateDbContextAsync();
                var track = await db.Tracks.AsNoTracking().SingleAsync();
                Assert.Multiple(() =>
                {
                    Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
                    Assert.That(track.CurrentPath, Is.EqualTo(primary));
                    Assert.That(track.ArchivePath, Is.EqualTo(archive));
                });
            }
            finally
            {
                File.SetUnixFileMode(archiveDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DeleteTrackFiles_WhenThePrimaryCannotBeDeleted_ClearsTheGoneArchivePointer()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("needs POSIX directory permissions");

        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-rmprim-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var (trackId, primary, archive) = await SeedTrackWithArchiveAsync(factory, musicDir);
            var store = NewStore(factory);
            var primaryDir = Path.GetDirectoryName(primary)!;
            await using (var seed = await factory.CreateDbContextAsync())
            {
                var row = await seed.Tracks.SingleAsync();
                row.IsHardLink = true;
                await seed.SaveChangesAsync();
            }

            File.SetUnixFileMode(primaryDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var outcome = await store.DeleteTrackFilesAsync("pl-rm", trackId);

                Assert.Multiple(() =>
                {
                    Assert.That(outcome.Deleted, Is.False);
                    Assert.That(outcome.Error, Does.Contain("could not delete"));
                    Assert.That(File.Exists(archive), Is.False,
                        "the archive copy was deleted before the primary was tried");
                    Assert.That(File.Exists(primary), Is.True,
                        "the row still points at a real file");
                });
                await using var db = await factory.CreateDbContextAsync();
                var track = await db.Tracks.AsNoTracking().SingleAsync();
                Assert.Multiple(() =>
                {
                    Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
                    Assert.That(track.CurrentPath, Is.EqualTo(primary));
                    Assert.That(track.ArchivePath, Is.Null,
                        "the pointer to the deleted archive is cleared");
                    Assert.That(track.IsHardLink, Is.True,
                        "the primary is still a hardlink twin; only the archive went");
                });
            }
            finally
            {
                File.SetUnixFileMode(primaryDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A playlist folder with a downloaded track, a real file and an archive copy.</summary>
    private static async Task<(int TrackId, string Primary, string Archive)> SeedTrackWithArchiveAsync(
        IDbContextFactory<MelodyBridgeDbContext> factory, string root)
    {
        var playlistDir = Path.Combine(root, "Removable");
        var archiveDir = Path.Combine(root, "archive", "Removable");
        Directory.CreateDirectory(playlistDir);
        Directory.CreateDirectory(archiveDir);
        var primary = Path.Combine(playlistDir, "mel-rm.mp3");
        var archive = Path.Combine(archiveDir, "mel-rm.mp3");
        await File.WriteAllTextAsync(primary, "primary bytes");
        await File.WriteAllTextAsync(archive, "archive bytes");

        await using var db = await factory.CreateDbContextAsync();
        var track = new TrackEntity
        {
            MelodyId = "mel-rm",
            Title = "Removable",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = primary,
            ArchivePath = archive,
        };
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-rm",
            Name = "Removable",
            SourceUrl = "stub:rm",
            TargetDirectory = playlistDir,
            ArchiveDirectory = archiveDir,
            Tracks = new List<TrackEntity> { track },
        });
        await db.SaveChangesAsync();
        return (track.Id, primary, archive);
    }

    /// <summary>Two playlists with the same pending track, for the dedup flow.</summary>
    private static async Task SeedSharedTrackPlaylistsAsync(IDbContextFactory<MelodyBridgeDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var (id, name) in new[] { ("pl-road", "Road Trip"), ("pl-chill", "Chill") })
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = id,
                Name = name,
                SourceUrl = "stub:" + id,
                SyncMode = "Additive",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-shared",
                        ExternalId = "t-shared",
                        ExternalPlatform = "Spotify",
                        Title = "Shared Song",
                        Artist = "Artist A",
                        DownloadStatus = "pending",
                        Position = 0,
                    },
                },
            });
        }
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task DownloadMissing_ExplicitFolder_WinsOverMusicPath()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-music2-{Guid.NewGuid():N}");
        var ownDir = Path.Combine(Path.GetTempPath(), $"mb-default-own-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var plugin = new FileWritingDownloader();
            var store = NewStore(factory, plugin);
            await new SettingsStore(factory).SetAsync("music_path", musicDir);

            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-own",
                    Name = "Own Folder",
                    SourceUrl = "stub:pl",
                    TargetDirectory = ownDir,
                    SyncMode = "Additive",
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "mel-o1",
                            Title = "Own Folder Song",
                            Artist = "Artist A",
                            DownloadStatus = "pending",
                            Position = 0,
                        },
                    },
                });
                await db.SaveChangesAsync();
            }

            var (downloaded, failed) = await store.DownloadMissingAsync("pl-own");

            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            Assert.That(plugin.OutputDirectories, Has.All.EqualTo(ownDir),
                "the playlist's own folder must beat the music_path default");

            await using var checkDb = await factory.CreateDbContextAsync();
            var track = await checkDb.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-o1");
            Assert.That(Path.GetDirectoryName(track.CurrentPath), Is.EqualTo(ownDir));
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(ownDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task AddOrRefresh_NewPlaylist_PrefillsTargetDirectoryFromMusicPath()
    {
        var dbPath = NewDbPath();
        var musicDir = Path.Combine(Path.GetTempPath(), $"mb-default-new-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await new SettingsStore(factory).SetAsync("music_path", musicDir);
            var provider = new StubMutableSourceProvider(
                new Track
                {
                    Title = "New Playlist Song",
                    Artist = "Artist A",
                    SongID = new SongID(Platform.Spotify, "t1"),
                    PlatformSongID = new SongID(Platform.Spotify, "t1"),
                });
            var store = new PlaylistStore(factory,
                new[] { provider },
                new Application.Services.DownloadManager(
                    new StubRegistry(Array.Empty<IDownloader>()),
                    NullLogger<Application.Services.DownloadManager>.Instance),
                NullLogger<PlaylistStore>.Instance);

            // No folder passed: the store must prefill the default so the
            // first download does not surprise.
            var added = await store.AddOrRefreshAsync("stub:pl");

            Assert.That(added.TargetDirectory, Is.EqualTo(musicDir),
                "a new playlist without a folder gets the music_path default");

            // Re-sync with no folder argument: an existing playlist keeps
            // its stored value.
            provider.Tracks.Add(new Track
            {
                Title = "Second Song",
                Artist = "Artist B",
                SongID = new SongID(Platform.Spotify, "t2"),
                PlatformSongID = new SongID(Platform.Spotify, "t2"),
            });
            var refreshed = await store.AddOrRefreshAsync("stub:pl");
            Assert.That(refreshed.TargetDirectory, Is.EqualTo(musicDir),
                "re-sync keeps the stored folder (no null overwrite)");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    /// <summary>Stub provider serving a mutable track list, like the sync-mode tests.</summary>
    private sealed class StubMutableSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;
        public List<Track> Tracks;
        public string? LastRequestedIdentifier;

        public StubMutableSourceProvider(params Track[] tracks) => Tracks = tracks.ToList();

        public bool CanHandle(string sourceIdentifier) => sourceIdentifier.Contains("stub:", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
        {
            LastRequestedIdentifier = sourceIdentifier;
            return Task.FromResult(new Playlist
            {
                Id = "stub-playlist",
                Name = "Stub Playlist",
                Tracks = Tracks.ToList(),
            });
        }

    }
}
