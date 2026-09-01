using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class FfprobeDiscoveryTests
{
    [Test]
    public void FindBinary_LocatesFfmpegAndFfprobe()
    {
        Assert.That(SpectrumAnalyzer.FindBinary("ffmpeg"), Is.Not.Null, "ffmpeg on PATH");
        Assert.That(SpectrumAnalyzer.FindFfprobe(), Is.Not.Null, "ffprobe discoverable");
    }
}
