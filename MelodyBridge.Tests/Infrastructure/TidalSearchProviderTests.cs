using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tidal;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Tidal search provider and its public catalogue client against the
/// real response shapes: ISRC lookup, text fallback, token refresh, rate
/// limiting. No live network.
/// </summary>
[TestFixture]
public class TidalSearchProviderTests
{
    private static readonly string IsrcJson = """
    {
      "limit": 10,
      "offset": 0,
      "totalNumberOfItems": 1,
      "items": [
        {
          "id": 35986110,
          "title": "Back In Black",
          "duration": 256,
          "isrc": "AUAP08000046",
          "streamReady": true,
          "audioQuality": "LOSSLESS",
          "mediaMetadata": { "tags": ["LOSSLESS", "HIRES_LOSSLESS"] },
          "artist": { "id": 945, "name": "AC/DC" },
          "artists": [ { "id": 945, "name": "AC/DC" } ]
        }
      ]
    }
    """;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Token() => Json("""{"access_token":"jwt-token","expires_in":3600}""");

    private static TidalPublicCatalog Catalog(ScriptedHandler handler)
        => new(new HttpClient(handler), NullLogger<TidalPublicCatalog>.Instance);

    private static TidalPublicCatalog.TidalPublicTrack Track(
        long id, string title, string artist, string? isrc = null, bool ready = true)
        => new(id, title, artist, TimeSpan.FromSeconds(256), isrc, ready,
            "LOSSLESS", new[] { "LOSSLESS" });

    // ── Catalogue parsing ────────────────────────────────────────────────

    [Test]
    public void ParseTracks_ReadsTheTidalShape()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(IsrcJson);
        var tracks = TidalPublicCatalog.ParseTracks(doc.RootElement);

        Assert.That(tracks, Has.Count.EqualTo(1));
        Assert.That(tracks[0].Id, Is.EqualTo(35986110));
        Assert.That(tracks[0].Title, Is.EqualTo("Back In Black"));
        Assert.That(tracks[0].Artist, Is.EqualTo("AC/DC"));
        Assert.That(tracks[0].Isrc, Is.EqualTo("AUAP08000046"));
        Assert.That(tracks[0].Duration, Is.EqualTo(TimeSpan.FromSeconds(256)));
        Assert.That(tracks[0].Tags, Does.Contain("HIRES_LOSSLESS"));
        Assert.That(tracks[0].SourceUrl,
            Is.EqualTo("https://tidal.com/browse/track/35986110"));
    }

    [Test]
    public async Task IsrcSearch_FetchesTokenAndTracks_AndCachesTheAnswer()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = request =>
            request.RequestUri!.AbsoluteUri.Contains("auth.odskyler.workers.dev")
                ? Token()
                : Json(IsrcJson);
        var catalog = Catalog(handler);

        var first = await catalog.SearchByIsrcAsync("AUAP08000046", "US");
        var second = await catalog.SearchByIsrcAsync("AUAP08000046", "US");

        Assert.That(first.Status, Is.EqualTo(TidalPublicCatalog.SearchStatus.Ok));
        Assert.That(first.Tracks, Has.Count.EqualTo(1));
        Assert.That(second.Tracks, Has.Count.EqualTo(1));
        Assert.That(handler.Requests.Count(r => r.Contains("auth.odskyler")), Is.EqualTo(1),
            "the token is cached");
        Assert.That(handler.Requests.Count(r => r.Contains("api.tidal.com")), Is.EqualTo(1),
            "an identical successful ISRC lookup is cached for a few minutes");
    }

    [Test]
    public async Task IsrcSearch_RejectedTokenIsNotReportedAsANetworkFailure()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = request =>
            request.RequestUri!.AbsoluteUri.Contains("auth.odskyler.workers.dev")
                ? Token()
                : Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized);
        var catalog = Catalog(handler);

        var result = await catalog.SearchByIsrcAsync("AUAP08000046", "NL");

        Assert.That(result.Status, Is.EqualTo(TidalPublicCatalog.SearchStatus.Rejected));
        Assert.That(result.Message, Does.Contain("rejected"));
    }

    [Test]
    public async Task IsrcSearch_RateLimitIsItsOwnStatus()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = request =>
            request.RequestUri!.AbsoluteUri.Contains("auth.odskyler.workers.dev")
                ? Token()
                : Json("""{}""", HttpStatusCode.TooManyRequests);
        var catalog = Catalog(handler);

        var result = await catalog.SearchByIsrcAsync("AUAP08000046", "US");

        Assert.That(result.Status, Is.EqualTo(TidalPublicCatalog.SearchStatus.RateLimited));
    }

    // ── Ranking and provider metadata ────────────────────────────────────

    [Test]
    public void RankExact_AcceptsADifferentTitleSpelling()
    {
        var query = new TrackQuery("AC/DC", "Back In Black",
            new DownloadQuality(AudioFormat.Flac), TimeSpan.FromSeconds(255),
            "AUAP08000046");
        var hit = TidalSearchProvider.RankExact(
            new[] { Track(35986110, "Back in Black (Remastered)", "AC/DC", "AUAP08000046") },
            query);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Title, Is.EqualTo("Back in Black (Remastered)"));
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(hit.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/35986110"));
    }

    [Test]
    public void Rank_DropsKaraokeAndUnplayableTracks()
    {
        var query = new TrackQuery("AC/DC", "Back In Black", DownloadQuality.Any);
        var hit = TidalSearchProvider.Rank(new[]
        {
            Track(1, "Back In Black", "AC/DC"),
            Track(2, "Back In Black (Karaoke Version)", "Karaoke Hits"),
            Track(3, "Back In Black", "AC/DC", ready: false),
        }, query);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/1"));
    }

    [Test]
    public void ProviderDeclaresBothTidalSourcesAndTheirData()
    {
        using var handler = new ScriptedHandler();
        var provider = new TidalSearchProvider(
            Catalog(handler), new StubPluginConfig(), NullLogger<TidalSearchProvider>.Instance);

        Assert.That(provider.Id, Is.EqualTo("tidal"));
        Assert.That(provider.Data, Is.EqualTo(SearchData.Isrc | SearchData.Text));
        Assert.That(provider.Targets.Select(t => t.DownloaderId),
            Is.EquivalentTo(new[] { "tidal", "rhythmax" }));
        Assert.That(provider.Targets.All(t => t.Data.HasFlag(SearchData.Isrc)), Is.True);
    }

    [Test]
    public void SearchProviderRegistry_RoutesBothTidalSourcesToTheProvider()
    {
        using var handler = new ScriptedHandler();
        var provider = new TidalSearchProvider(
            Catalog(handler), new StubPluginConfig(), NullLogger<TidalSearchProvider>.Instance);
        var registry = new SearchProviderRegistry(
            new[] { provider }, NullLogger<SearchProviderRegistry>.Instance);

        Assert.That(registry.ForDownloader("tidal"), Is.SameAs(provider));
        Assert.That(registry.ForDownloader("rhythmax"), Is.SameAs(provider));
        Assert.That(registry.ForDownloader("ytdlp"), Is.Null);
    }
}
