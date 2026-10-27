using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Deezer catalogue search provider: ISRC-first requests and the shared
/// ranking. The deemix download side lives in <see cref="DeemixDownloaderTests"/>.
/// </summary>
[TestFixture]
public class DeemixSearchProviderTests
{
    private static DeezerClient.DeezerTrack Track(
        long id, string title, string artist, int duration, string? isrc = null)
        => new(id, title, artist, TimeSpan.FromSeconds(duration), isrc);

    private static DeemixSearchProvider Create(ScriptedHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com") };
        return new DeemixSearchProvider(new DeezerClient(http, NullLogger<DeezerClient>.Instance));
    }

    [Test]
    public async Task Search_WithAnIsrc_AsksDeezerForTheCodeFirst()
    {
        var handler = new ScriptedHandler()
            .On("/search?q=isrc", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":222,"title":"The Last Airbender","duration":217,"isrc":"USRC17607839","artist":{"name":"SLVL"}}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            })
            .On("/search?q=", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":999,"title":"The Last Airbender","duration":300,"isrc":"OTHER00000001","artist":{"name":"Avatar Soundtrack"}}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        var hit = await Create(handler).SearchAsync("deemix", new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests.First().Url, Does.Contain("isrc"),
                "the code is the first request");
            Assert.That(handler.Requests.First().Url, Does.Contain("USRC17607839"));
            Assert.That(handler.Requests, Has.Count.EqualTo(1),
                "the exact code resolved it; the text search must not run");
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://www.deezer.com/track/222"));
        });
    }

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutARequest()
    {
        var handler = new ScriptedHandler();
        var hit = await Create(handler).SearchAsync("qobuz",
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void Rank_PicksTheRealMatch_NotTheFirstItem()
    {
        var hit = DeemixSearchProvider.Rank(new[]
        {
            Track(111, "Never Gonna Give You Up (Cake Mix)", "Rick Astley", 347),
            Track(222, "Never Gonna Give You Up", "Rick Astley", 213, "GBARL0600786"),
        }, new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://www.deezer.com/track/222"));
    }

    [Test]
    public void Rank_ExactIsrc_WinsOverACloserTitle()
    {
        var hit = DeemixSearchProvider.Rank(new[]
        {
            Track(111, "Never Gonna Give You Up", "Rick Astley", 213, "OTHERISRC001"),
            Track(222, "Never Gonna Give You Up (Remastered 2007)", "Rick Astley", 214, "GBARL0600786"),
        }, new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(213), Isrc: "GBARL0600786"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://www.deezer.com/track/222"),
            "an exact ISRC is the recording itself");
    }

    [Test]
    public void Rank_Empty_IsNull()
        => Assert.That(DeemixSearchProvider.Rank(Array.Empty<DeezerClient.DeezerTrack>(),
            new TrackQuery("A", "T", DownloadQuality.Any)), Is.Null);
}
