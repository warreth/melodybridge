using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

[TestFixture]
public class ProviderQualitiesTests
{
    [Test]
    public void SquidWtf_HasExpectedQualities()
    {
        var qualities = ProviderQualities.SquidWtf;
        Assert.Multiple(() =>
        {
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.AAC));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.OPUS));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 24 && q.Format == MediaType.FLAC));
        });
    }

    [Test]
    public void Lucida_HasExpectedQualities()
    {
        var qualities = ProviderQualities.Lucida;
        Assert.Multiple(() =>
        {
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 128 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 16 && q.Format == MediaType.FLAC));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 24 && q.Format == MediaType.FLAC));
        });
    }

    [Test]
    public void DoubleDouble_HasExpectedQualities()
    {
        var qualities = ProviderQualities.DoubleDouble;
        Assert.Multiple(() =>
        {
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 16 && q.Format == MediaType.FLAC));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 24 && q.Format == MediaType.FLAC));
        });
    }

    [Test]
    public void Monochrome_HasExpectedQualities()
    {
        var qualities = ProviderQualities.Monochrome;
        Assert.Multiple(() =>
        {
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.AAC));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 16 && q.Format == MediaType.FLAC));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 24 && q.Format == MediaType.FLAC));
        });
    }

    [Test]
    public void YouTubeDlp_HasExpectedQualities()
    {
        var qualities = ProviderQualities.YouTubeDlp;
        Assert.Multiple(() =>
        {
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 128 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 192 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 320 && q.Format == MediaType.MP3));
            Assert.That(qualities, Has.Some.Matches<TrackQuality>(q => q.Bitrate == 256 && q.Format == MediaType.AAC));
        });
    }
}
