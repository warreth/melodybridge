using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The YouTube search provider's routing and metadata. The flat playlist
/// parser has its own tests; these pin the source boundary so the provider
/// never starts yt-dlp for another downloader or an empty query.
/// </summary>
[TestFixture]
public class YtDlpSearchProviderTests
{
    private static YtDlpSearchProvider Create()
        => new(NullLogger<YtDlpSearchProvider>.Instance);

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutAProcess()
    {
        var provider = Create();

        Assert.That(await provider.SearchAsync("soundcloud",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Null);
        Assert.That(await provider.SearchAllAsync("soundcloud",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Empty);
    }

    [Test]
    public async Task Search_EmptyText_IsAMissWithoutAProcess()
    {
        var provider = Create();

        Assert.That(await provider.SearchAsync("ytdlp",
            new TrackQuery("", "", DownloadQuality.Any)), Is.Null);
    }

    [Test]
    public void Provider_DeclaresTheSelfSearchTierAndItsTarget()
    {
        var provider = Create();

        Assert.Multiple(() =>
        {
            Assert.That(provider.Data, Is.EqualTo(SearchData.Text));
            Assert.That(provider.Tier, Is.EqualTo(SearchTier.SelfSearch));
            Assert.That(provider.Targets.Single().DownloaderId, Is.EqualTo("ytdlp"));
            Assert.That(provider.Targets.Single().Tier, Is.EqualTo(SearchTier.SelfSearch));
        });
    }
}
