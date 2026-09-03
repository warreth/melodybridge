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
/// DownloadTrackAsync (the per-track download button) with a REAL plugin
/// that writes REAL tagged files, a REAL PlaylistStore and REAL SQLite.
/// The core guarantee under test: clicking download on one song never
/// touches the other tracks of the playlist.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DownloadTrackAsyncTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-one-{test}-{Guid.NewGuid():N}.db");

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
    /// Real-ish plugin: writes an actual (tiny) MP3 file and tags it -
    /// the same side effects YtDlpDownloader has. Fails for titles that
    /// start with "FAIL".
    /// </summary>
    private sealed class FileWritingDownloader : IDownloader
    {
        public string Id => "file-writer";
        public string Name => "File Writer (test)";
        public List<(string artist, string title)> SearchQueries = new();

        public PluginCapabilities Capabilities => PluginCapabilities.Any;
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
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");

            // Minimal but valid MP3: empty ID3v2.3 header + silence frames,
            // so the integrity gate accepts it.
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

    private static PlaylistStore NewStore(IDbContextFactory<MelodyBridgeDbContext> factory, IDownloader downloader)
    {
        var downloadManager = new Application.Services.DownloadManager(
            new StubRegistry(new[] { downloader }),
            NullLogger<Application.Services.DownloadManager>.Instance);
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

    /// <summary>Seeds one playlist with pending tracks; returns their row ids in order.</summary>
    private static async Task<List<int>> SeedPlaylistAsync(
        IDbContextFactory<MelodyBridgeDbContext> factory, string targetDir,
        params (string id, string title, string artist)[] tracks)
    {
        await using var db = factory.CreateDbContext();
        var entity = new PlaylistEntity
        {
            Id = "pl-one",
            Name = "One Click",
            SourceUrl = "stub:pl",
            TargetDirectory = targetDir,
            SyncMode = "Additive",
        };
        var pos = 0;
        foreach (var (id, title, artist) in tracks)
            entity.Tracks.Add(new TrackEntity
            {
                MelodyId = $"mel-{id}",
                ExternalId = id,
                ExternalPlatform = "Spotify",
                Title = title,
                Artist = artist,
                DownloadStatus = "pending",
                Position = pos++,
            });
        db.Playlists.Add(entity);
        await db.SaveChangesAsync();
        return await db.Tracks.Where(t => t.PlaylistEntityId == "pl-one").Select(t => t.Id).ToListAsync();
    }

    [Test]
    public async Task DownloadTrack_DownloadsOnlyThatTrack()
    {
        var dbPath = NewDbPath();
        var outDir = Path.Combine(Path.GetTempPath(), $"mb-one-out-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var downloader = new FileWritingDownloader();
            var store = NewStore(factory, downloader);
            var ids = await SeedPlaylistAsync(factory, outDir,
                ("a", "First Song", "Artist A"),
                ("b", "Second Song", "Artist B"),
                ("c", "Third Song", "Artist C"));

            var result = await store.DownloadTrackAsync(ids[1]);

            Assert.That(result, Is.EqualTo("downloaded"));

            await using var db = await factory.CreateDbContextAsync();
            var rows = await db.Tracks.Where(t => t.PlaylistEntityId == "pl-one").OrderBy(t => t.Position).ToListAsync();
            Assert.That(rows[0].DownloadStatus, Is.EqualTo("pending"), "track 1 stays untouched");
            Assert.That(rows[1].DownloadStatus, Is.EqualTo("downloaded"), "the clicked track downloads");
            Assert.That(rows[2].DownloadStatus, Is.EqualTo("pending"), "track 3 stays untouched");

            // The plugin searched for exactly one song - the clicked one.
            Assert.That(downloader.SearchQueries, Is.EqualTo(new[] { ("Artist B", "Second Song") }));
            // And the file exists on disk.
            Assert.That(System.IO.File.Exists(rows[1].CurrentPath), Is.True);
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Test]
    public async Task DownloadTrack_FailedTrack_ReportsFailureAndKeepsOthersPending()
    {
        var dbPath = NewDbPath();
        var outDir = Path.Combine(Path.GetTempPath(), $"mb-one-out-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new FileWritingDownloader());
            var ids = await SeedPlaylistAsync(factory, outDir,
                ("a", "FAIL Song", "Artist A"),
                ("b", "Good Song", "Artist B"));

            var result = await store.DownloadTrackAsync(ids[0]);

            Assert.That(result, Is.EqualTo("failed"));
            await using var db = await factory.CreateDbContextAsync();
            var failed = await db.Tracks.FindAsync(ids[0]);
            Assert.That(failed!.DownloadStatus, Is.EqualTo("failed"));
            Assert.That(failed.DownloadError, Is.Not.Null);
            var other = await db.Tracks.FindAsync(ids[1]);
            Assert.That(other!.DownloadStatus, Is.EqualTo("pending"), "the failure never leaks to other tracks");
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Test]
    public async Task DownloadTrack_AlreadyClaimed_DoesNotDoubleDownload()
    {
        var dbPath = NewDbPath();
        var outDir = Path.Combine(Path.GetTempPath(), $"mb-one-out-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            var downloader = new FileWritingDownloader();
            var store = NewStore(factory, downloader);
            var ids = await SeedPlaylistAsync(factory, outDir,
                ("a", "In Progress Song", "Artist A"));

            // Simulate a running batch that already claimed the track.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var t = await db.Tracks.FindAsync(ids[0]);
                t!.DownloadStatus = "in_progress";
                await db.SaveChangesAsync();
            }

            var result = await store.DownloadTrackAsync(ids[0]);

            Assert.That(result, Is.EqualTo("claimed-by-another-run"));
            Assert.That(downloader.SearchQueries, Is.Empty, "no search happens when another run owns the track");
        }
        finally
        {
            File.Delete(dbPath);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Test]
    public async Task DownloadTrack_UnknownTrack_ReturnsNotFound()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new FileWritingDownloader());

            var result = await store.DownloadTrackAsync(999999);

            Assert.That(result, Is.EqualTo("not-found"));
        }
        finally
        {
            File.Delete(dbPath);
        }
    }
}
