using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The default download folder for playlists without their own folder:
/// DownloadMissingAsync must fall back to the music_path setting (the
/// "standaard downloadmap") and AddOrRefreshAsync must prefill it for new
/// playlists. Real SQLite, real PlaylistStore, plugin that writes real files.
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
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
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

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(title, artist, $"https://example.com/{artist}-{title}", TimeSpan.FromSeconds(180)));

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
        => new(factory,
            new ISourceProvider[] { new StubSourceProvider() },
            new Application.Services.DownloadManager(
                new StubRegistry(downloaders),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;
        public bool CanHandle(string sourceIdentifier) => sourceIdentifier.Contains("stub:");
        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier) => throw new NotImplementedException();
        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
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
    public async Task DownloadMissing_NoPlaylistFolder_FallsBackToMusicPathSetting()
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

            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            Assert.That(plugin.OutputDirectories, Has.All.EqualTo(musicDir),
                "the plugin must receive the music_path setting as the output folder");

            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-d1");
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(Path.GetDirectoryName(track.CurrentPath), Is.EqualTo(musicDir),
                "the file must land inside the music_path directory");
            Assert.That(System.IO.File.Exists(track.CurrentPath), Is.True);
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(musicDir, recursive: true); } catch { /* best effort */ }
        }
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

        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
    }
}
