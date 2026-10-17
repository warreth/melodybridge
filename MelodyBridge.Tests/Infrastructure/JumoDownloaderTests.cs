using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Jumo;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Jumo plugin parsing tests against response shapes captured from the
/// live service: search items, track and album ids, descriptor manifests
/// and the source identifier round trip. No network access needed.
/// </summary>
[TestFixture]
public class JumoDownloaderTests
{
    private const string SearchJson = """
    {
      "query": "daft punk one more time",
      "albums": { "total": 2, "items": [
        { "id": "0886443927087", "title": "Random Access Memories" }
      ] },
      "tracks": { "total": 3, "items": [
        {
          "id": 9140024, "title": "One More Time", "duration": 320.4, "isrc": "GBDUW0000053",
          "hires": true, "maximum_bit_depth": 24,
          "performer": { "name": "Daft Punk" },
          "album": { "id": "0886443927087", "title": "Random Access Memories", "artist": { "name": "Daft Punk" } }
        },
        {
          "id": 1068442, "title": "One More Time (Remix)", "duration": 320.4, "isrc": null,
          "hires": false, "maximum_bit_depth": 16,
          "artist": { "name": "Some DJ" },
          "album": { "id": "999", "title": "Remixes", "artist": { "name": "Some DJ" } }
        },
        {
          "id": 555, "title": "One More Time (Live)", "duration": 300.0, "isrc": null,
          "hires": false,
          "album": { "title": "No album id" }
        }
      ] }
    }
    """;

    [Test]
    public void ParseSearchTracks_PicksTheStudioTrack_AndDropsRemixes()
    {
        var query = new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any, TimeSpan.FromSeconds(320));

        var hit = JumoDownloader.ParseSearchTracks(SearchJson, query.Artist, query.Title, query.ExpectedDuration);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Title, Is.EqualTo("One More Time"));
        Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
        Assert.That(hit.SourceUrl, Is.EqualTo("jumo:0886443927087:9140024"));
        Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(320.4)).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void ParseSearchTracks_IsrcNarrowsToTheExactRecording()
    {
        // Same title twice with different recordings: the ISRC pool must
        // win over the text ranking.
        const string json = """
        {
          "tracks": { "items": [
            { "id": 1, "title": "One More Time", "duration": 320, "isrc": "OTHER00000001",
              "performer": { "name": "Cover Band" }, "album": { "id": "a" } },
            { "id": 2, "title": "One More Time", "duration": 321, "isrc": "GBDUW0000053",
              "performer": { "name": "Daft Punk" }, "album": { "id": "b" } }
          ] }
        }
        """;

        var hit = JumoDownloader.ParseSearchTracks(json, "Daft Punk", "One More Time", null, "GBDUW0000053");

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("jumo:b:2"), "the exact ISRC recording must win over titles");
    }

    [Test]
    public void ParseSearchTracks_EntriesWithoutAlbumId_AreSkipped()
    {
        const string json = """
        {
          "tracks": { "items": [
            { "id": 555, "title": "One More Time", "duration": 320,
              "performer": { "name": "Daft Punk" }, "album": { "title": "No album id" } }
          ] }
        }
        """;

        // Without an album id the source identifier cannot be built; the
        // search misses instead of producing a download that cannot resolve.
        var hit = JumoDownloader.ParseSearchTracks(json, "Daft Punk", "One More Time");

        Assert.That(hit, Is.Null);
    }

    [Test]
    public void TryParseSourceUrl_RoundTrips()
    {
        var url = JumoDownloader.SourceUrl("0886443927087", "9140024");
        Assert.That(url, Is.EqualTo("jumo:0886443927087:9140024"));

        Assert.That(JumoDownloader.TryParseSourceUrl(url, out var albumId, out var trackId), Is.True);
        Assert.That(albumId, Is.EqualTo("0886443927087"));
        Assert.That(trackId, Is.EqualTo("9140024"));

        Assert.That(JumoDownloader.TryParseSourceUrl("https://jumo-dl.pages.dev/xyz", out _, out _), Is.False);
        Assert.That(JumoDownloader.TryParseSourceUrl("jumo:only-two", out _, out _), Is.False);
        Assert.That(JumoDownloader.TryParseSourceUrl("", out _, out _), Is.False);
    }

    [Test]
    public void ParseManifest_ReadsTheStreamDescriptor()
    {
        const string json = """
        {
          "track_id": "9140024",
          "previewDetected": false,
          "qb": {
            "track_id": "9140024", "format_id": "27", "effective_format": "7", "region": "CA",
            "duration": 274.4, "n_segments": 28, "preview": false,
            "content_key": "327e101a8c4db79b17f5d57b501021ae",
            "url_template": "https://streaming-qobuz-sec.akamaized.net/file?uid=1&eid=2&s=$SEGMENT$&hmac=abc",
            "container": "flac",
            "segmentProxyUrl": "https://jumo-dl.pages.dev/ygbxgtasashrkcylu?p=xyz"
          }
        }
        """;

        var manifest = JumoDownloader.ParseManifest(json);

        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest!.EffectiveFormat, Is.EqualTo("7"));
        Assert.That(manifest.Container, Is.EqualTo("flac"));
        Assert.That(manifest.SegmentCount, Is.EqualTo(28));
        Assert.That(manifest.Preview, Is.False);
        Assert.That(manifest.ContentKey, Has.Length.EqualTo(32));
        Assert.That(manifest.UrlTemplate, Does.Contain("$SEGMENT$"));
    }

    [Test]
    public void ParseManifest_PreviewStream_IsFlagged()
    {
        const string json = """
        { "qb": { "effective_format": "5", "container": "mp3", "preview": true,
          "content_key": "00112233445566778899aabbccddeeff",
          "url_template": "https://example.com/$SEGMENT$", "n_segments": 2 } }
        """;

        Assert.That(JumoDownloader.ParseManifest(json)!.Preview, Is.True);
    }

    [Test]
    public void ConfigFields_OfferRegionAndFlacQuality()
    {
        var downloader = new JumoDownloader(
            new JumoClient(new HttpClient(new UnusedHandler()), Microsoft.Extensions.Logging.Abstractions.NullLogger<JumoClient>.Instance),
            new StubPluginConfig(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JumoDownloader>.Instance);

        Assert.That(downloader.Id, Is.EqualTo("jumo"));
        Assert.That(downloader.ConfigFields.Any(f => f.Key == JumoDownloader.RegionKey), Is.True);

        var quality = downloader.ConfigFields.Single(f => f.Key == JumoDownloader.FlacQualityKey);
        Assert.That(quality.Options, Is.Not.Null);
        Assert.That(quality.Options!.Select(o => o.Value), Is.EqualTo(new[] { "6", "7", "27" }));
    }

    [Test]
    public void Caps_DeclareLosslessAndMp3()
    {
        Assert.That(JumoDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(JumoDownloader.Caps.ServesControlled(new DownloadQuality(AudioFormat.Flac)), Is.True);
        Assert.That(JumoDownloader.Caps.ServesControlled(new DownloadQuality(AudioFormat.Mp3, 320, 320)), Is.True);
        Assert.That(JumoDownloader.Caps.ServesControlled(new DownloadQuality(AudioFormat.Opus)), Is.False);
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("no HTTP expected in this test");
    }
}
