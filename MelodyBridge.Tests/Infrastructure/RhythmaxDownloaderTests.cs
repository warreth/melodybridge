using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Rhythmax;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Rhythmax's Tidal tier mapping and URL grammar. The real download is in
/// the live suite; these tests pin the decisions that keep a FLAC request
/// from asking for AAC and a Space Saver request from asking for FLAC.
/// </summary>
[TestFixture]
public class RhythmaxDownloaderTests
{
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

    private static RhythmaxDownloader Downloader(ScriptedHandler? handler = null)
    {
        handler ??= new ScriptedHandler();
        var client = new RhythmaxClient(new HttpClient(handler), "https://hifi.test",
            NullLogger<RhythmaxClient>.Instance);
        return new RhythmaxDownloader(client, new StubPluginConfig(),
            NullLogger<RhythmaxDownloader>.Instance);
    }

    [Test]
    public void QualityFor_MapsTheTidalTiers()
    {
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Flac)),
            Is.EqualTo("LOSSLESS"));
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Aac, null, 320)),
            Is.EqualTo("HIGH"));
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Aac, null, 96)),
            Is.EqualTo("LOW"));
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Aac, 96, 320)),
            Is.EqualTo("HIGH"));
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Aac, 96, 160)),
            Is.EqualTo("LOW"));
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Aac, 128, 160)),
            Is.Null, "96 is below the floor and 320 is above the cap");
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Aac, 200, 260)),
            Is.Null, "neither 96 nor 320 fits");
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Mp3)),
            Is.Null, "Tidal has no MP3");
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Opus)),
            Is.Null);
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Auto, null, 320)),
            Is.EqualTo("HIGH"), "a capped auto request takes the best lossy tier, not FLAC");
        Assert.That(RhythmaxDownloader.QualityFor(new DownloadQuality(AudioFormat.Auto, null, 160)),
            Is.EqualTo("LOW"));
        Assert.That(RhythmaxDownloader.QualityFor(DownloadQuality.Any),
            Is.EqualTo("LOSSLESS"));
    }

    [Test]
    public void Capabilities_MatchTheTwoRealAacTiers()
    {
        Assert.That(RhythmaxDownloader.Caps.CanServe(
            new DownloadQuality(AudioFormat.Aac, null, 96)), Is.True);
        Assert.That(RhythmaxDownloader.Caps.CanServe(
            new DownloadQuality(AudioFormat.Aac, null, 320)), Is.True);
        Assert.That(RhythmaxDownloader.Caps.CanServe(
            new DownloadQuality(AudioFormat.Aac, 96, 320)), Is.True);
        Assert.That(RhythmaxDownloader.Caps.CanServe(
            new DownloadQuality(AudioFormat.Aac, 128, 160)), Is.False,
            "there is no Tidal tier between 96 and 320");
        Assert.That(RhythmaxDownloader.Caps.CanServe(
            new DownloadQuality(AudioFormat.Flac)), Is.True);
        Assert.That(RhythmaxDownloader.Caps.CanServe(
            new DownloadQuality(AudioFormat.Mp3)), Is.False);
    }

    [Test]
    public void TryExtractTrackId_AcceptsTheTidalUrlShapes()
    {
        Assert.That(RhythmaxDownloader.TryExtractTrackId(
            "https://tidal.com/browse/track/35986110", out var browse), Is.True);
        Assert.That(browse, Is.EqualTo(35986110));
        Assert.That(RhythmaxDownloader.TryExtractTrackId(
            "https://tidal.com/track/35986110?u", out var plain), Is.True);
        Assert.That(plain, Is.EqualTo(35986110));
        Assert.That(RhythmaxDownloader.TryExtractTrackId(
            "https://listen.tidal.com/track/35986110", out var listen), Is.True);
        Assert.That(listen, Is.EqualTo(35986110));
        Assert.That(RhythmaxDownloader.TryExtractTrackId("35986110", out var bare), Is.True);
        Assert.That(bare, Is.EqualTo(35986110));
        Assert.That(RhythmaxDownloader.TryExtractTrackId(
            "https://tidal.com/browse/album/35986110", out _), Is.False);
        Assert.That(RhythmaxDownloader.TryExtractTrackId(
            "https://example.test/track/35986110", out _), Is.False);
    }

    [Test]
    public async Task Download_RejectsForeignUrlsWithoutTouchingTheService()
    {
        var handler = new ScriptedHandler();
        var result = await Downloader(handler).DownloadAsync(
            "https://example.test/not-tidal", "/tmp/unused", "melody",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("not a Tidal track URL"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Download_RejectsAnMp3RequestWithoutStartingADownload()
    {
        var handler = new ScriptedHandler();
        var result = await Downloader(handler).DownloadAsync(
            "https://tidal.com/browse/track/35986110", "/tmp/unused", "melody",
            new DownloadQuality(AudioFormat.Mp3));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("cannot serve"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Availability_ReportsTheServicesOwnDeadPoolReason()
    {
        var handler = new ScriptedHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"DEAD","active":0}""", Encoding.UTF8, "application/json"),
            },
        };
        var downloader = Downloader(handler);

        Assert.That(await downloader.IsAvailableAsync(), Is.False);
        Assert.That(downloader.LastUnavailableReason, Does.Contain("no working Tidal account"));
    }

    [Test]
    public void SearchInfo_IsProviderBackedAndCarriesNoOwnSearch()
    {
        var downloader = Downloader();

        Assert.That(downloader.SearchInfo.Mode, Is.EqualTo(DownloaderSearchMode.Provider));
        Assert.That(downloader.SearchInfo.Data.HasFlag(SearchData.Isrc), Is.True);
        Assert.That(downloader.SearchInfo.Data.HasFlag(SearchData.Text), Is.True);
        Assert.That(downloader.SearchAsync(new TrackQuery("a", "b", DownloadQuality.Any))
            .GetAwaiter().GetResult(), Is.Null);
    }
}
