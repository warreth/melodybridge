using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// End to end ISRC enrichment: a track without an ISRC gets one from the
/// Deezer-backed resolver before the download, the plugin search receives
/// it, and the resolved value persists on the row (so the next run does
/// not look it up again).
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class IsrcResolutionTests
{
    private string _dbPath = null!;
    private string _dir = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-isrc-store-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-isrc-store-{Guid.NewGuid():N}");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond(request));
    }

    /// <summary>Writes a real MP3 and records the ISRC it was asked for.</summary>
    private sealed class RecordingDownloader : IDownloader
    {
        public string Id => "recording";
        public string Name => "Recording (test)";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public string? LastIsrc;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            LastIsrc = query.Isrc;
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, "https://example.com/rec",
                    query.ExpectedDuration));
        }

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3(), ct);
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    private sealed class StubRegistry(params IDownloader[] downloaders) : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => downloaders;
        public IDownloader? Get(string id) => downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
        public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private PlaylistStore NewStore(IDownloader downloader, IsrcResolver resolver)
        => new(_factory,
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new StubRegistry(downloader), NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            isrcResolver: resolver);

    private async Task<string> SeedPlaylistAsync(string melodyId, string title, string artist, long? durationMs)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var playlist = new PlaylistEntity
        {
            Name = "ISRC Fixture",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TargetDirectory = _dir,
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = melodyId,
                    Title = title,
                    Artist = artist,
                    DurationMs = durationMs,
                    DownloadStatus = "pending",
                    Position = 0,
                },
            },
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return playlist.Id;
    }

    [Test]
    public async Task DownloadMissing_ResolvesTheIsrc_AndThePluginAndRowSeeIt()
    {
        // No duration on the row: the stub file is a ~1s silence MP3 and
        // the integrity gate compares durations. The resolver ranking by
        // title/artist (and duration when present) is unit-tested.
        var playlistId = await SeedPlaylistAsync(
            "mel-isrc-1", "Never Gonna Give You Up", "Rick Astley", null);

        // Deezer answers the exact recording with its ISRC.
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "data": [
                {
                  "title": "Never Gonna Give You Up",
                  "duration": 211,
                  "isrc": "GBARL0600786",
                  "artist": { "name": "Rick Astley" }
                }
              ]
            }
            """, Encoding.UTF8, "application/json"),
        };
        var resolver = new IsrcResolver(new HttpClient(handler), NullLogger<IsrcResolver>.Instance);
        var downloader = new RecordingDownloader();
        var store = NewStore(downloader, resolver);

        var (downloaded, failed) = await store.DownloadMissingAsync(playlistId);

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
        Assert.That(downloader.LastIsrc, Is.EqualTo("GBARL0600786"),
            "the ISRC-indexed plugins search by the resolved code");

        // Fresh context: the resolved ISRC persisted on the row.
        await using var check = await _factory.CreateDbContextAsync();
        var track = await check.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-isrc-1");
        Assert.That(track.Isrc, Is.EqualTo("GBARL0600786"),
            "the next run must not look the ISRC up again");
        Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
    }

    [Test]
    public async Task DownloadMissing_UnresolvableIsrc_DownloadsWithoutOne()
    {
        var playlistId = await SeedPlaylistAsync(
            "mel-isrc-2", "Some Obscure Track", "Nobody Knows", null);

        // Deezer has nothing for this track.
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
        };
        var resolver = new IsrcResolver(new HttpClient(handler), NullLogger<IsrcResolver>.Instance);
        var downloader = new RecordingDownloader();
        var store = NewStore(downloader, resolver);

        var (downloaded, failed) = await store.DownloadMissingAsync(playlistId);

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)),
            "no ISRC is not a failure: the text search still runs");
        Assert.That(downloader.LastIsrc, Is.Null);
    }
}
