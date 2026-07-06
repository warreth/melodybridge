using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

[TestFixture]
public class MappingTests
{
    [Test]
    public void GetPlatformsForQuality_Null_ReturnsEmpty()
    {
        var results = PlatformQualityMapper.GetPlatformsForQuality(null!);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void GetPlatformsForQuality_320Mp3_ReturnsSoundcloudAndQobuz()
    {
        var quality = new TrackQuality(320, MediaType.MP3);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results, Has.Some.Matches<(Platform p, DownloadSource s)>(x => x.p == Platform.Soundcloud && x.s == DownloadSource.squidwtf));
            Assert.That(results, Has.Some.Matches<(Platform p, DownloadSource s)>(x => x.p == Platform.Qobuz && x.s == DownloadSource.squidwtf));
        });
    }

    [Test]
    public void GetPlatformsForQuality_24Flac_ReturnsQobuzAmazonTidal()
    {
        var quality = new TrackQuality(24, MediaType.FLAC);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Exactly(3).Items);
            Assert.That(results, Has.Some.Matches<(Platform p, DownloadSource s)>(x => x.p == Platform.Qobuz && x.s == DownloadSource.squidwtf));
            Assert.That(results, Has.Some.Matches<(Platform p, DownloadSource s)>(x => x.p == Platform.AmazonMusic && x.s == DownloadSource.squidwtf));
            Assert.That(results, Has.Some.Matches<(Platform p, DownloadSource s)>(x => x.p == Platform.Tidal && x.s == DownloadSource.squidwtf));
        });
    }

    [Test]
    public void GetPlatformsForQuality_320Opus_ReturnsAmazonMusic()
    {
        var quality = new TrackQuality(320, MediaType.OPUS);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.One.Items);
            Assert.That(results[0], Is.EqualTo((Platform.AmazonMusic, DownloadSource.squidwtf)));
        });
    }

    [Test]
    public void GetPlatformsForQuality_320Aac_ReturnsTidal()
    {
        var quality = new TrackQuality(320, MediaType.AAC);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.One.Items);
            Assert.That(results[0], Is.EqualTo((Platform.Tidal, DownloadSource.squidwtf)));
        });
    }

    [Test]
    public void GetPlatformsForQuality_192Flac_ReturnsTidal()
    {
        var quality = new TrackQuality(192, MediaType.FLAC);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.One.Items);
            Assert.That(results[0], Is.EqualTo((Platform.Tidal, DownloadSource.squidwtf)));
        });
    }

    [Test]
    public void GetPlatformsForQuality_128Mp3_ReturnsEmpty()
    {
        var quality = new TrackQuality(128, MediaType.MP3);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void GetPlatformsForQuality_16Flac_ReturnsEmpty()
    {
        var quality = new TrackQuality(16, MediaType.FLAC);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void GetPlatformsForQuality_256Aac_ReturnsEmpty()
    {
        var quality = new TrackQuality(256, MediaType.AAC);
        var results = PlatformQualityMapper.GetPlatformsForQuality(quality);
        Assert.That(results, Is.Empty);
    }
}
