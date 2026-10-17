using System.Net;
using System.Text;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The download pipeline end to end with real files and a real SQLite
/// database: a cover URL and a lyrics lookup enrich the finished file, a
/// dead cover URL changes nothing, and art a plugin already embedded is
/// never overwritten. The HTTP boundary is scripted; everything else is
/// the shipped code.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class MetadataEnrichmentTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-enrich-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "enrich.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A real 1x1 PNG, the smallest thing TagLib accepts as a picture.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            return Task.FromResult(Respond(url));
        }
    }

    private sealed class StubDownloader : IDownloader
    {
        private readonly byte[] _bytes;
        public StubDownloader(byte[] bytes) => _bytes = bytes;

        public string Id => "stub";
        public string Name => "Stub (test)";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, "stub://track", TimeSpan.FromSeconds(2),
                    MatchConfidence: MatchConfidence.High));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            File.WriteAllBytes(path, _bytes);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
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

    private PlaylistStore NewStore(StubHandler handler, byte[] sourceBytes)
    {
        var manager = new DownloadManager(
            new StubRegistry(new IDownloader[] { new StubDownloader(sourceBytes) }),
            NullLogger<DownloadManager>.Instance);
        // One handler serves both the cover client and the lyrics client.
        var coverFetcher = new CoverArtFetcher(new HttpClient(handler), NullLogger<CoverArtFetcher>.Instance);
        var lyricsResolver = new LyricsResolver(new HttpClient(handler), NullLogger<LyricsResolver>.Instance);
        return new PlaylistStore(
            _factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance,
            accountProviders: null, settings: null, hardLinks: null, isrcResolver: null,
            coverArt: coverFetcher, lyrics: lyricsResolver);
    }

    private async Task SeedPlaylistAsync(string coverUrl, bool withLyricsAlready)
    {
        var dir = Path.Combine(_root, "music");
        Directory.CreateDirectory(dir);
        await using var db = _factory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-enrich",
            Name = "Enrich",
            SourceUrl = "https://example.com/enrich",
            TargetDirectory = dir,
            PreferredFormat = "auto",
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "spotify:enrich-1",
                    ExternalId = "enrich-1",
                    ExternalPlatform = "Spotify",
                    Title = "Around the World",
                    Artist = "Daft Punk",
                    Album = "Homework",
                    DurationMs = 2000,
                    Position = 0,
                    DownloadStatus = "pending",
                    CoverUrl = coverUrl,
                    Lyrics = withLyricsAlready ? "already known" : null,
                },
            },
        });
        await db.SaveChangesAsync();
    }

    private TrackEntity ReadTrack()
    {
        using var db = _factory.CreateDbContext();
        return db.Tracks.AsNoTracking().Single(t => t.MelodyId == "spotify:enrich-1");
    }

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private static HttpResponseMessage Lyrics(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"plainLyrics\":\"{text}\"}}", Encoding.UTF8, "application/json"),
    };

    [Test]
    public async Task Download_WithCoverUrlAndNoLyrics_EmbedsBoth()
    {
        var cover = TinyPng();
        await SeedPlaylistAsync("https://cdn.example/cover.jpg", withLyricsAlready: false);
        var handler = new StubHandler
        {
            Respond = url => url.Contains("cdn.example")
                ? Image(cover)
                : Lyrics("Around the world"),
        };
        var store = NewStore(handler, TestAudio.MinimalMp3());

        var (downloaded, failed) = await store.DownloadMissingAsync("pl-enrich");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
        var row = ReadTrack();
        Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.Multiple(() =>
        {
            Assert.That(row.Lyrics, Is.EqualTo("Around the world"),
                "the resolved lyrics are stored on the row");

            var file = TagLib.File.Create(row.CurrentPath!);
            Assert.That(file.Tag.Pictures, Has.Length.EqualTo(1), "the cover is embedded");
            Assert.That(file.Tag.Pictures[0].Data.Data, Is.EqualTo(cover),
                "the fetched bytes land unchanged");
            Assert.That(file.Tag.Lyrics, Does.Contain("Around the world"));
        });
    }

    [Test]
    public async Task Download_DeadCoverUrl_StillSucceedsWithoutArt()
    {
        await SeedPlaylistAsync("https://cdn.example/gone.jpg", withLyricsAlready: false);
        var handler = new StubHandler
        {
            Respond = url => url.Contains("cdn.example")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Lyrics("words"),
        };
        var store = NewStore(handler, TestAudio.MinimalMp3());

        var (downloaded, failed) = await store.DownloadMissingAsync("pl-enrich");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)),
            "metadata enrichment must never fail a downloaded track");
        var row = ReadTrack();
        var file = TagLib.File.Create(row.CurrentPath!);
        Assert.Multiple(() =>
        {
            Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(file.Tag.Pictures, Is.Empty, "no art when there is none to fetch");
            Assert.That(file.Tag.Lyrics, Does.Contain("words"), "the lyrics still landed");
        });
    }

    [Test]
    public async Task Download_FileWithExistingArt_IsNotOverwritten()
    {
        var originalCover = TinyPng();
        // The source file arrives with art already embedded, as qobuz-dlp
        // and deemix produce them.
        var sourcePath = Path.Combine(_root, "source.mp3");
        File.WriteAllBytes(sourcePath, TestAudio.MinimalMp3());
        MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteTags(sourcePath, coverArt: originalCover);
        var sourceBytes = File.ReadAllBytes(sourcePath);

        await SeedPlaylistAsync("https://cdn.example/other.jpg", withLyricsAlready: true);
        var handler = new StubHandler { Respond = _ => Image(new byte[] { 9, 9, 9 }) };
        var store = NewStore(handler, sourceBytes);

        var (downloaded, failed) = await store.DownloadMissingAsync("pl-enrich");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
        var row = ReadTrack();
        var file = TagLib.File.Create(row.CurrentPath!);
        Assert.Multiple(() =>
        {
            Assert.That(file.Tag.Pictures, Has.Length.EqualTo(1));
            Assert.That(file.Tag.Pictures[0].Data.Data, Is.EqualTo(originalCover),
                "the plugin's own art wins");
            Assert.That(handler.Requests.Any(u => u.Contains("cdn.example")), Is.False,
                "no cover request when the file already has art");
            Assert.That(row.Lyrics, Is.EqualTo("already known"),
                "existing lyrics are not looked up again");
        });
    }
}
