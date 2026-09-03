using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Tests.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// DownloadMissingAsync end-to-end with a REAL plugin that writes REAL
/// tagged files, a REAL PlaylistStore and a REAL SQLite database.
/// No mocks on the download path: the plugin writes to disk and tags via
/// TaglibHelper, exactly like YtDlpDownloader does.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DownloadMissingAsyncTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-missing-{test}-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// Real-ish plugin: writes an actual (tiny) MP3 file and tags it with
    /// MELODY_ID + artist/title: the same side effects YtDlpDownloader has.
    /// Fails for tracks whose title starts with "FAIL".
    /// </summary>
    private sealed class FileWritingDownloader : IDownloader
    {
        public string Id => "file-writer";
        public string Name => "File Writer (test)";
        public List<(string artist, string title)> SearchQueries = new();

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
        {
            SearchQueries.Add((artist, title));
            if (title.StartsWith("FAIL", StringComparison.Ordinal)) return Task.FromResult<DownloaderSearchHit?>(null);
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(title, artist, $"https://example.com/{artist}-{title}", TimeSpan.FromSeconds(180)));
        }

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            if (sourceUrl.Contains("FAIL", StringComparison.Ordinal))
                return new DownloaderDownloadResult(false, null, "simulated plugin failure");

            Directory.CreateDirectory(outputDirectory);
            var fileName = $"{melodyId}.mp3";
            var path = Path.Combine(outputDirectory, fileName);

            // Write a minimal but valid MP3: empty ID3v2.3 header + silence
            // frames (MPEG-1 Layer III, 128 kbps, 44.1 kHz, 417 bytes each).
            // TagLib requires valid frames to parse the file.
            var id3Header = new byte[]
            {
                0x49, 0x44, 0x33, 0x03, 0x00, 0x00, // "ID3" v2.3, no flags
                0x00, 0x00, 0x00, 0x00,             // tag size = 0 (syncsafe)
            };
            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;

            var fileBytes = new byte[id3Header.Length + frame.Length * 40];
            id3Header.CopyTo(fileBytes, 0);
            for (var i = 0; i < 40; i++)
                frame.CopyTo(fileBytes, id3Header.Length + frame.Length * i);

            await System.IO.File.WriteAllBytesAsync(path, fileBytes, ct);

            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(path, melodyId!);
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory,
        params IDownloader[] downloaders)
    {
        var registry = new StubRegistry(downloaders);
        var downloadManager = new Application.Services.DownloadManager(
            registry, NullLogger<Application.Services.DownloadManager>.Instance);
        return new PlaylistStore(
            factory,
            new ISourceProvider[] { new StubSourceProvider() },
            downloadManager,
            NullLogger<PlaylistStore>.Instance);
    }

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

    private static async Task<PlaylistEntity> SeedPlaylistAsync(
        IDbContextFactory<MelodyBridgeDbContext> factory,
        string targetDir,
        params (string id, string title, string artist)[] tracks)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entity = new PlaylistEntity
        {
            Id = "pl-missing",
            Name = "Download Me",
            SourceUrl = "stub:pl",
            TargetDirectory = targetDir,
            SyncMode = "Additive",
        };
        var pos = 0;
        foreach (var (id, title, artist) in tracks)
        {
            entity.Tracks.Add(new TrackEntity
            {
                MelodyId = $"mel-{id}",
                ExternalId = id,
                ExternalPlatform = "Spotify",
                Title = title,
                Artist = artist,
                DownloadStatus = "pending",
                Position = pos++,
                PlaylistSnapshotId = null,
            });
        }
        db.Playlists.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    [Test]
    public async Task DownloadMissing_WritesTaggedFilesAndUpdatesDb()
    {
        var dbPath = NewDbPath();
        var outDir = Path.Combine(Path.GetTempPath(), $"mb-missing-out-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var plugin = new FileWritingDownloader();
            var store = NewStore(factory, plugin);

            await SeedPlaylistAsync(factory, outDir,
                ("t1", "Good Song", "Artist A"),
                ("t2", "FAIL Song", "Artist B"));

            var (downloaded, failed) = await store.DownloadMissingAsync("pl-missing");

            Assert.That(downloaded, Is.EqualTo(1));
            Assert.That(failed, Is.EqualTo(1));

            // Every assertion reads back through a fresh DbContext.
            await using var db = await factory.CreateDbContextAsync();
            var tracks = await db.Tracks.AsNoTracking().OrderBy(t => t.Position).ToListAsync();

            var good = tracks.Single(t => t.ExternalId == "t1");
            Assert.That(good.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(good.CurrentPath, Is.Not.Null);
            Assert.That(System.IO.File.Exists(good.CurrentPath), Is.True, "the file must actually exist");
            Assert.That(Path.GetFileName(good.CurrentPath), Is.EqualTo("mel-t1.mp3"));

            // The MELODY_ID tag must be readable from the real bytes on disk.
            var tagMelodyId = MelodyBridge.Infrastructure.Tagging.TaglibHelper.ReadMelodyId(good.CurrentPath!);
            Assert.That(tagMelodyId, Is.EqualTo("mel-t1"), "file on disk must carry the MELODY_ID tag");

            var bad = tracks.Single(t => t.ExternalId == "t2");
            Assert.That(bad.DownloadStatus, Is.EqualTo("failed"));
            Assert.That(bad.DownloadError, Is.Not.Null.And.Not.Empty);
            Assert.That(bad.CurrentPath, Is.Null);
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DownloadMissing_RequiresTargetDirectory()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new FileWritingDownloader());

            // Seed WITHOUT a TargetDirectory and with the music_path
            // default blanked out: every fallback is gone, the run must
            // refuse to start rather than write into a random folder.
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-nodir",
                    Name = "No Dir",
                    SourceUrl = "stub:pl",
                    SyncMode = "Additive",
                });
                await db.SaveChangesAsync();
            }
            await new SettingsStore(factory).SetAsync("music_path", "");

            // An explicit empty-string override is the one combination with
            // no fallback left: the run refuses to start. (A null folder now
            // falls back to music_path: see DefaultDownloadPathTests.)
            Assert.That(async () => await store.DownloadMissingAsync("pl-nodir", outputDirectoryOverride: " "),
                Throws.InvalidOperationException.With.Message.Contains("download folder"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task DownloadMissing_SecondRun_SkipsDownloadedTracks()
    {
        var dbPath = NewDbPath();
        var outDir = Path.Combine(Path.GetTempPath(), $"mb-missing-out2-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var plugin = new FileWritingDownloader();
            var store = NewStore(factory, plugin);

            await SeedPlaylistAsync(factory, outDir, ("t1", "Good Song", "Artist A"));

            await store.DownloadMissingAsync("pl-missing");
            var pluginQueryCountAfterFirstRun = plugin.SearchQueries.Count;

            // Second run: t1 is already downloaded, must not search again.
            var (downloaded, failed) = await store.DownloadMissingAsync("pl-missing");
            Assert.That((downloaded, failed), Is.EqualTo((0, 0)));
            Assert.That(plugin.SearchQueries.Count, Is.EqualTo(pluginQueryCountAfterFirstRun),
                "downloaded tracks must not be re-downloaded");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
