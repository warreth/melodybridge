using MelodyBridge.Infrastructure.Downloaders;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The SoundCloud floor gate reads the audio bitrate from yt-dlp's JSON.
/// yt-dlp reports abr and tbr in kbps already; the old code divided tbr by
/// 1000, so a 160 kbps track was rejected as 0 kbps.
/// </summary>
[TestFixture]
public class SoundCloudBitrateTests
{
    [Test]
    public void Abr_IsAlreadyKbps()
        => Assert.That(SoundCloudDownloader.ReadAudioBitrateKbps("{\"abr\": 128.0}"), Is.EqualTo(128));

    [Test]
    public void Tbr_IsAlreadyKbps()
        => Assert.That(SoundCloudDownloader.ReadAudioBitrateKbps("{\"tbr\": 160.6}"), Is.EqualTo(161));

    [Test]
    public void Abr_WinsOverTbr()
        => Assert.That(SoundCloudDownloader.ReadAudioBitrateKbps("{\"abr\": 320.0, \"tbr\": 128.0}"), Is.EqualTo(320));

    [Test]
    public void Vbr_IsTheVideoBitrate_NeverUsed()
        => Assert.That(SoundCloudDownloader.ReadAudioBitrateKbps("{\"vbr\": 5000.0}"), Is.Null);

    [Test]
    public void MissingFields_ReturnNull()
        => Assert.That(SoundCloudDownloader.ReadAudioBitrateKbps("{\"title\": \"x\"}"), Is.Null);

    [Test]
    public void Garbage_ReturnsNull()
        => Assert.That(SoundCloudDownloader.ReadAudioBitrateKbps("not json"), Is.Null);
}
