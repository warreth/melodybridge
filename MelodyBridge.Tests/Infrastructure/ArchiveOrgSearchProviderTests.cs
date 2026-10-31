using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The archive.org search provider against the live response shapes: five
/// search rows, metadata per item, one MP3 candidate and the quality band.
/// The matching item is the fourth row, which pins the "scan every
/// returned row" fix.
/// </summary>
[TestFixture]
public class ArchiveOrgSearchProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string SearchJson()
    {
        var docs = string.Join(",", Enumerable.Range(1, 5)
            .Select(i => "{\"identifier\":\"item-" + i + "\",\"title\":\"Item " + i + "\"}"));
        return "{\"response\":{\"numFound\":5,\"docs\":[" + docs + "]}}";
    }

    private static HttpResponseMessage Metadata(HttpRequestMessage request)
    {
        var identifier = request.RequestUri!.AbsolutePath.Split('/').Last();
        return identifier == "item-4"
            ? Json("""
                {"metadata":{"title":"Für Elise","creator":"Johanne Stockmarr"},
                 "files":[{"name":"01 - Fur Elise.mp3","title":"Für Elise",
                           "length":"3:03","bitrate":"192"}]}
                """)
            : Json("""{"metadata":{},"files":[]}""");
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static ArchiveOrgSearchProvider Create(StubHandler handler)
        => new(new HttpClient(handler), NullLogger<ArchiveOrgSearchProvider>.Instance);

    [Test]
    public async Task Search_ScansEveryReturnedRow_AndRanksTheMp3()
    {
        var handler = new StubHandler();
        handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("advancedsearch")
            ? Json(SearchJson())
            : Metadata(request);
        var provider = Create(handler);

        var hit = await provider.SearchAsync("archiveorg", new TrackQuery(
            "Johanne Stockmarr", "Für Elise", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(hit!.Title, Is.EqualTo("Für Elise"));
            Assert.That(hit.Artist, Is.EqualTo("Johanne Stockmarr"));
            Assert.That(hit.SourceUrl, Does.Contain("/download/item-4/"));
            Assert.That(hit.BitrateKbps, Is.EqualTo(192));
            Assert.That(handler.Requests.Any(url => url.Contains("/metadata/item-4")), Is.True,
                "the fourth search row is not dropped");
        });
    }

    [Test]
    public async Task Search_QualityBand_RejectsAnUnfittingMp3()
    {
        var handler = new StubHandler();
        handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("advancedsearch")
            ? Json(SearchJson())
            : Metadata(request);
        var provider = Create(handler);

        var hit = await provider.SearchAsync("archiveorg", new TrackQuery(
            "Johanne Stockmarr", "Für Elise",
            new DownloadQuality(AudioFormat.Mp3, 320, 320)));

        Assert.That(hit, Is.Null, "a 192 kbps file cannot satisfy a 320 kbps request");
    }

    [Test]
    public void Search_CallerCancellation_Propagates()
    {
        var provider = new ArchiveOrgSearchProvider(
            new HttpClient(new CancellingHandler()),
            NullLogger<ArchiveOrgSearchProvider>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.CatchAsync<OperationCanceledException>(() => provider.SearchAsync(
            "archiveorg", new TrackQuery("Artist", "Song", DownloadQuality.Any), cts.Token));
    }

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutARequest()
    {
        var handler = new StubHandler();
        var provider = Create(handler);

        var hit = await provider.SearchAsync("ytdlp",
            new TrackQuery("Artist", "Song", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Search_NoMp3Files_IsAMiss()
    {
        var handler = new StubHandler();
        handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("advancedsearch")
            ? Json(SearchJson())
            : Json("""{"metadata":{},"files":[{"name":"cover.jpg"}]}""");
        var provider = Create(handler);

        Assert.That(await provider.SearchAsync("archiveorg",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Null);
    }
}
