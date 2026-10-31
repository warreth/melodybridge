using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The SoundCloud search provider's routing and metadata. The shared flat
/// playlist parser is covered separately; these pin the source boundary so
/// an empty query or another downloader never starts yt-dlp.
/// </summary>
[TestFixture]
public class SoundCloudSearchProviderTests
{
    private static SoundCloudSearchProvider Create()
        => new(NullLogger<SoundCloudSearchProvider>.Instance);

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutAProcess()
    {
        var provider = Create();

        Assert.That(await provider.SearchAsync("ytdlp",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Null);
        Assert.That(await provider.SearchAllAsync("ytdlp",
            new TrackQuery("Artist", "Song", DownloadQuality.Any)), Is.Empty);
    }

    [Test]
    public async Task Search_EmptyText_IsAMissWithoutAProcess()
    {
        var provider = Create();

        Assert.That(await provider.SearchAsync("soundcloud",
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
            Assert.That(provider.Targets.Single().DownloaderId, Is.EqualTo("soundcloud"));
            Assert.That(provider.Targets.Single().Tier, Is.EqualTo(SearchTier.SelfSearch));
        });
    }
}
