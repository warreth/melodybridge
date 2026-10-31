using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using static MelodyBridge.Tests.Infrastructure.SlskdTestSupport;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Soulseek search provider against the real slskd v0.x response
/// shapes: search creation and polling, locked files, quality bands, the
/// peer naming helpers and the raw transfer reference. No live network.
/// </summary>
[TestFixture]
public class SlskdSearchProviderTests
{
    [Test]
    public async Task Search_CreatesPollsAndRanksTheResponses()
    {
        var handler = new StubHttpMessageHandler();
        var statusCalls = 0;
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && url.EndsWith("/api/v0/searches"))
                return Ok(new { id = "search-1", state = "InProgress" });
            if (url.EndsWith("/api/v0/searches/search-1"))
                return ++statusCalls == 1
                    ? Ok(new { id = "search-1", state = "InProgress" })
                    : Ok(new { id = "search-1", state = "Completed, ResponseLimitReached" });
            if (url.EndsWith("/api/v0/searches/search-1/responses"))
                return Ok(new object[]
                {
                    Response("peer-remix", freeSlot: true,
                        SlskFile("Music\\Various\\Around the World (Remix).mp3", 5_000_000, bitRate: 320)),
                    Response("peer-flac", freeSlot: true,
                        SlskFile("Music\\Daft Punk\\Homework\\07 Daft Punk - Around the World.flac", 48_000_000)),
                });
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var provider = CreateProvider(handler);

        var hit = await provider.SearchAsync("slskd", new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.Artist, Is.EqualTo("Daft Punk"));
            Assert.That(hit.Title, Is.EqualTo("Around the World"));
            Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(200)),
                "length seconds map onto the duration");
            Assert.That(hit.SourceUrl, Does.StartWith("slskd:peer-flac:"),
                "the lossless file with the free slot wins the tie");
            Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        });

        Assert.That(handler.RequestedUrls.Any(u => u.StartsWith("DELETE") && u.Contains("/searches/search-1")),
            Is.True, "the search is cleaned up after use");
    }

    [Test]
    public async Task Search_SkipsLockedFiles_AndOtherContainers()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (url.Contains("/responses"))
                return Ok(new object[]
                {
                    Response("peer", freeSlot: false,
                        SlskFile("Music\\Artist - Song.flac", 40_000_000, locked: true),
                        SlskFile("Music\\Artist - Song.wav", 60_000_000),
                        SlskFile("Music\\Artist - Song.mp3", 8_000_000, bitRate: 192)),
                });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "Completed" });
        };
        var provider = CreateProvider(handler);

        var hit = await provider.SearchAsync("slskd",
            new TrackQuery("Artist", "Song", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Does.StartWith("slskd:peer:"), "only the mp3 was usable");
    }

    [Test]
    public async Task Search_RespectsTheQualityBand()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (url.Contains("/responses"))
                return Ok(new object[]
                {
                    Response("peer", freeSlot: true,
                        SlskFile("Music\\Artist - Song.mp3", 4_000_000, bitRate: 128),
                        SlskFile("Music\\Artist - Song.flac", 40_000_000)),
                });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "Completed" });
        };
        var provider = CreateProvider(handler);

        // A 320 request must not settle for the 128 kbps file.
        var hit = await provider.SearchAsync("slskd", new TrackQuery(
            "Artist", "Song", new DownloadQuality(AudioFormat.Mp3, MinKbps: 320, MaxKbps: 320)));

        Assert.That(hit, Is.Null, "neither the 128 kbps file nor a FLAC fits an exact 320 band");
    }

    [Test]
    public async Task Search_NoUsableResponses_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (url.Contains("/responses")) return Ok(Array.Empty<object>());
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "Completed" });
        };
        var provider = CreateProvider(handler);

        Assert.That(await provider.SearchAsync("slskd",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Null);
    }

    [Test]
    public async Task Search_ThatNeverCompletes_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "InProgress" });
        };
        var provider = CreateProvider(handler);

        Assert.That(await provider.SearchAsync("slskd",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)),
            Is.Null, "a search that never completes is a miss");
    }

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutARequest()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler);

        Assert.That(await provider.SearchAsync("ytdlp",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Null);
        Assert.That(handler.RequestedUrls, Is.Empty);
    }

    [TestCase("Music\\Daft Punk\\Homework\\07 Daft Punk - Around the World.flac", "Daft Punk", "Around the World")]
    [TestCase("Artist - Song.mp3", "Artist", "Song")]
    [TestCase("01 Song.flac", null, "Song")]
    [TestCase("01. Song.flac", null, "Song")]
    [TestCase("Song.mp3", null, "Song")]
    public void SplitArtistTitle_HandlesPeerNaming(string filename, string? artist, string title)
    {
        var (a, t) = SlskdSearchProvider.SplitArtistTitle(filename);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(artist));
            Assert.That(t, Is.EqualTo(title));
        });
    }

    [TestCase("a.flac", AudioFormat.Flac)]
    [TestCase("a.FLAC", AudioFormat.Flac)]
    [TestCase("a.mp3", AudioFormat.Mp3)]
    [TestCase("a.m4a", AudioFormat.Aac)]
    [TestCase("a.ogg", null)]
    [TestCase("a.txt", null)]
    public void ContainerFor_MapsDeclaredContainers(string filename, AudioFormat? expected)
        => Assert.That(SlskdSearchProvider.ContainerFor(filename), Is.EqualTo(expected));

    [TestCase("Completed", true)]
    [TestCase("Completed, ResponseLimitReached", true)]
    [TestCase("InProgress", false)]
    public void SearchIsComplete_KnowsTheStates(string state, bool expected)
        => Assert.That(SlskdSearchProvider.SearchIsComplete(state), Is.EqualTo(expected));
}
