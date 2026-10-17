using System.Net;
using System.Text;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The Re-tag library action on real files and a real SQLite database: it
/// fills only what is missing, stores resolved lyrics on the row, never
/// touches a file that is already complete, counts a vanished file as
/// failed, and never writes anything it does not have.
/// </summary>
[TestFixture]
public class RetagLibraryTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-retag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "retag.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

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

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private static HttpResponseMessage Lyrics(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"plainLyrics\":\"{text}\"}}", Encoding.UTF8, "application/json"),
    };

    private PlaylistStore NewStore(StubHandler? handler)
    {
        var manager = new DownloadManager(new EmptyDownloaderRegistry(),
            NullLogger<DownloadManager>.Instance);
        CoverArtFetcher? cover = null;
        LyricsResolver? lyrics = null;
        if (handler is not null)
        {
            cover = new CoverArtFetcher(new HttpClient(handler), NullLogger<CoverArtFetcher>.Instance);
            lyrics = new LyricsResolver(new HttpClient(handler), NullLogger<LyricsResolver>.Instance);
        }
        return new PlaylistStore(
            _factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance,
            accountProviders: null, settings: null, hardLinks: null, isrcResolver: null,
            coverArt: cover, lyrics: lyrics);
    }

    /// <summary>A real, taggable mp3 on disk plus the matching row.</summary>
    private async Task<TrackEntity> SeedDownloadedTrackAsync(
        string? coverUrl = null, string? album = null, string? albumArtist = null,
        string? knownLyrics = null, bool fileExists = true)
    {
        var path = Path.Combine(_root, $"track-{Guid.NewGuid():N}.mp3");
        if (fileExists)
        {
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteTags(
                path, title: "Around the World", artist: "Daft Punk");
        }

        await using var db = _factory.CreateDbContext();
        var row = new TrackEntity
        {
            MelodyId = $"spotify:{Guid.NewGuid():N}",
            Title = "Around the World",
            Artist = "Daft Punk",
            Album = album,
            AlbumArtist = albumArtist,
            CoverUrl = coverUrl,
            Lyrics = knownLyrics,
            DurationMs = 2000,
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = path,
        };
        db.Tracks.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private TrackEntity ReadTrack(int id)
    {
        using var db = _factory.CreateDbContext();
        return db.Tracks.AsNoTracking().Single(t => t.Id == id);
    }

    [Test]
    public async Task Retag_FillsMissingCoverAndLyrics_AndStoresLyrics()
    {
        var cover = TinyPng();
        var row = await SeedDownloadedTrackAsync(
            coverUrl: "https://cdn.example/cover.jpg",
            album: "Homework", albumArtist: "Daft Punk");
        var handler = new StubHandler
        {
            Respond = url => url.Contains("cdn.example") ? Image(cover) : Lyrics("Around the world"),
        };
        var store = NewStore(handler);

        var (updated, skipped, failed) = await store.RetagLibraryAsync();

        Assert.Multiple(() =>
        {
            Assert.That((updated, skipped, failed), Is.EqualTo((1, 0, 0)));
            Assert.That(ReadTrack(row.Id).Lyrics, Is.EqualTo("Around the world"),
                "the resolved lyrics are stored on the row");

            var file = TagLib.File.Create(row.CurrentPath!);
            Assert.That(file.Tag.Pictures, Has.Length.EqualTo(1));
            Assert.That(file.Tag.Pictures[0].Data.Data, Is.EqualTo(cover));
            Assert.That(file.Tag.AlbumArtists, Does.Contain("Daft Punk"));
            Assert.That(file.Tag.Album, Is.EqualTo("Homework"));
            Assert.That(file.Tag.Lyrics, Does.Contain("Around the world"));
        });
    }

    [Test]
    public async Task Retag_CompleteFile_IsSkippedWithoutARequest()
    {
        var row = await SeedDownloadedTrackAsync(
            coverUrl: "https://cdn.example/cover.jpg",
            album: "Homework", albumArtist: "Daft Punk", knownLyrics: "known");
        // The file already carries everything the action could fill.
        MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteTags(
            row.CurrentPath!, album: "Homework", albumArtist: "Daft Punk",
            coverArt: TinyPng(), lyrics: "known");
        var handler = new StubHandler { Respond = _ => Image(TinyPng()) };
        var store = NewStore(handler);

        var (updated, skipped, failed) = await store.RetagLibraryAsync();

        Assert.Multiple(() =>
        {
            Assert.That((updated, skipped, failed), Is.EqualTo((0, 1, 0)));
            Assert.That(handler.Requests, Is.Empty,
                "nothing is fetched for a file that is already complete");
        });
    }

    [Test]
    public async Task Retag_VanishedFile_CountsAsFailed()
    {
        var row = await SeedDownloadedTrackAsync(
            coverUrl: "https://cdn.example/cover.jpg", fileExists: false);
        var store = NewStore(new StubHandler());

        var (updated, skipped, failed) = await store.RetagLibraryAsync();

        Assert.Multiple(() =>
        {
            Assert.That((updated, skipped, failed), Is.EqualTo((0, 0, 1)));
            Assert.That(ReadTrack(row.Id).DownloadStatus, Is.EqualTo("downloaded"),
                "re-tagging must not change download status");
        });
    }

    [Test]
    public async Task Retag_RowWithoutCoverOrResolver_SkipsWithoutARequest()
    {
        await SeedDownloadedTrackAsync();
        // No cover fetcher and no lyrics resolver at all: the file has
        // nothing to gain, and the action must say so instead of failing.
        var store = NewStore(handler: null);

        var (updated, skipped, failed) = await store.RetagLibraryAsync();

        Assert.That((updated, skipped, failed), Is.EqualTo((0, 1, 0)),
            "nothing to fill and nothing to fill it from");
    }

    [Test]
    public async Task Retag_DeadCoverUrl_StillWritesTheLyrics()
    {
        var row = await SeedDownloadedTrackAsync(coverUrl: "https://cdn.example/gone.jpg");
        var handler = new StubHandler
        {
            Respond = url => url.Contains("cdn.example")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Lyrics("words"),
        };
        var store = NewStore(handler);

        var (updated, skipped, failed) = await store.RetagLibraryAsync();

        Assert.Multiple(() =>
        {
            Assert.That((updated, skipped, failed), Is.EqualTo((1, 0, 0)));
            Assert.That(ReadTrack(row.Id).Lyrics, Is.EqualTo("words"));
            var file = TagLib.File.Create(row.CurrentPath!);
            Assert.That(file.Tag.Pictures, Is.Empty, "a dead cover URL embeds nothing");
            Assert.That(file.Tag.Lyrics, Does.Contain("words"));
        });
    }
}
