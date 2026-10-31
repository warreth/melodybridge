using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;
using static MelodyBridge.Tests.Infrastructure.FlacMusicTestSupport;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The FLAC Music search provider: platform fallback, the raw manual-picker
/// candidates, the quality-driven format choice and caller cancellation.
/// The downloader only consumes the URLs this provider returns.
/// </summary>
[TestFixture]
public class FlacMusicSearchProviderTests
{
    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task SearchAsync_OnePlatformFailureStillUsesTheOther()
    {
        using var handler = new FlacScriptedHandler();
        handler.Respond = (_, body) =>
            body is not null && body.Contains("platform=kuwo")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(SearchJson(Track(3880720, "Back In Black", "AC/DC", 255)));
        var provider = Provider(handler);

        var hit = await provider.SearchAsync("flacmusic", new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Flac)));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Does.Contain("/track/wyy/3880720"));
    }

    [Test]
    public async Task SearchAllAsync_ReturnsEveryPlatformCandidate()
    {
        using var handler = new FlacScriptedHandler();
        handler.Respond = (_, body) =>
        {
            var wyy = body is not null && body.Contains("platform=wyy");
            return Json(SearchJson(Track(
                wyy ? 3880720 : 336887, "Back In Black", "AC/DC", 255)));
        };
        var provider = Provider(handler);

        var hits = await provider.SearchAllAsync("flacmusic", new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Flac)));

        Assert.That(hits, Has.Count.EqualTo(2));
        Assert.That(hits.Select(h => h.SourceUrl).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task SearchAsync_PicksTheFormatForTheRequestedQuality()
    {
        using var handler = new FlacScriptedHandler();
        handler.Respond = (_, body) =>
        {
            var platform = body is not null && body.Contains("platform=wyy") ? "wyy" : "kuwo";
            return Json(SearchJson(
                Track(platform == "kuwo" ? 336887 : 3880720,
                    "Back In Black", "AC/DC", 255)));
        };
        var provider = Provider(handler);

        var high = await provider.SearchAsync("flacmusic", new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Auto, null, 320)));
        var saver = await provider.SearchAsync("flacmusic", new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Auto, null, 160)));
        var lossless = await provider.SearchAsync("flacmusic", new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Flac)));

        Assert.That(high, Is.Not.Null);
        Assert.That(high!.BitrateKbps, Is.EqualTo(320));
        Assert.That(saver!.BitrateKbps, Is.EqualTo(128));
        Assert.That(lossless!.BitrateKbps, Is.Null);
        Assert.That(lossless.SourceUrl, Does.Contain("flac%3A2000"));
    }

    [Test]
    public void SearchCallerCancellation_Propagates()
    {
        using var handler = new CancellingHandler();
        var client = new FlacMusicClient(new HttpClient(handler),
            NullLogger<FlacMusicClient>.Instance);
        var provider = new FlacMusicSearchProvider(
            new FlacMusicSession(client, new StubPluginConfig(),
                NullLogger<FlacMusicSession>.Instance),
            NullLogger<FlacMusicSearchProvider>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.CatchAsync<OperationCanceledException>(() =>
            provider.SearchAsync("flacmusic", new TrackQuery("AC/DC", "Back In Black",
                DownloadQuality.Any), cts.Token));
    }

    [Test]
    public async Task SearchAsync_ForAnotherSource_IsAMissWithoutARequest()
    {
        using var handler = new FlacScriptedHandler();
        var provider = Provider(handler);

        var hit = await provider.SearchAsync("ytdlp",
            new TrackQuery("Artist", "Song", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }
}
