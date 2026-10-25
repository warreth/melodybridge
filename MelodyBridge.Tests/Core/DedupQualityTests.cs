using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

/// <summary>
/// Dedup quality matching: a lossless container is the promise, so a
/// FLAC without a measured bitrate must still link. A lossy band needs a
/// measurement before a hardlink can claim it matches.
/// </summary>
[TestFixture]
public class DedupQualityTests
{
    [Test]
    public void FlacAnyBitrate_WithUnknownBitrate_Matches()
    {
        var quality = new DownloadQuality(AudioFormat.Flac);
        Assert.That(DedupQuality.Matches("flac", null, quality), Is.True,
            "a FLAC file is lossless; the missing bitrate does not change that");
    }

    [Test]
    public void FlacBand_WithUnknownBitrate_Matches()
    {
        var quality = new DownloadQuality(AudioFormat.Flac, 500, 1500);
        Assert.That(DedupQuality.Matches("flac", null, quality), Is.True,
            "bitrate bounds do not apply to a lossless container");
    }

    [Test]
    public void LossyBand_WithUnknownBitrate_DoesNotMatch()
    {
        var quality = new DownloadQuality(AudioFormat.Mp3, 192, 320);
        Assert.That(DedupQuality.Matches("mp3", null, quality), Is.False,
            "a lossy band cannot be verified without a measurement");
    }

    [Test]
    public void LossyNoBand_WithUnknownBitrate_Matches()
    {
        var quality = new DownloadQuality(AudioFormat.Mp3);
        Assert.That(DedupQuality.Matches("mp3", null, quality), Is.True,
            "the container is the only constraint");
    }

    [Test]
    public void WrongContainer_DoesNotMatch()
    {
        var quality = new DownloadQuality(AudioFormat.Flac);
        Assert.That(DedupQuality.Matches("opus", 128, quality), Is.False);
    }

    [Test]
    public void Describe_UnknownBitrate_SaysSo()
    {
        var mismatch = new DedupMatch("/x.flac", "flac", null, null, QualityMatches: false);
        Assert.That(mismatch.Describe(), Does.Contain("bitrate unknown"));
        Assert.That(mismatch.Describe(), Does.Contain("different from this playlist's target"));
    }
}
