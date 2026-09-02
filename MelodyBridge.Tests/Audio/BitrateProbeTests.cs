namespace MelodyBridge.Tests.Audio;

/// <summary>
/// BitrateProbe against real audio files generated with the real ffmpeg
/// binary: no stubbed processes, no canned JSON. The files are tiny
/// sine waves, one per container, exactly like the downloads produce.
///
/// The core regression: FLAC and Opus files carry no stream-level
/// bit_rate; only the format section has it. The probe must fall back
/// to the format value instead of reporting nothing.
/// </summary>
[TestFixture]
[Category("Live")]
public class BitrateProbeTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>One real file, made with the ffmpeg on PATH.</summary>
    private string Make(string args, string name)
    {
        var path = Path.Combine(_dir, name);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -v error -f lavfi -i sine=frequency=440:duration=3 {args} \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        Assert.That(proc.ExitCode, Is.EqualTo(0), $"ffmpeg failed for {name}: {stderr}");
        return path;
    }

    private static void RequireFfprobe()
    {
        if (MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffprobe is not installed on this machine");
    }

    [Test]
    public void Flac_FallsBackToFormatBitrate()
    {
        RequireFfprobe();
        var path = Make("-ar 44100", "tone.flac");

        var kbps = MelodyBridge.Infrastructure.Audio.BitrateProbe.MeasureKbps(path);

        Assert.That(kbps, Is.Not.Null,
            "flac has no stream bit_rate; the format value must be used instead");
        Assert.That(kbps, Is.GreaterThan(0));
    }

    [Test]
    public void Opus_FallsBackToFormatBitrate()
    {
        RequireFfprobe();
        var path = Make("", "tone.opus");

        var kbps = MelodyBridge.Infrastructure.Audio.BitrateProbe.MeasureKbps(path);

        Assert.That(kbps, Is.Not.Null,
            "opus has no stream bit_rate; the format value must be used instead");
        Assert.That(kbps, Is.GreaterThan(0));
    }

    [Test]
    public void Mp3_UsesStreamBitrate()
    {
        RequireFfprobe();
        var path = Make("-ar 44100", "tone.mp3");

        var kbps = MelodyBridge.Infrastructure.Audio.BitrateProbe.MeasureKbps(path);

        Assert.That(kbps, Is.Not.Null);
        Assert.That(kbps, Is.GreaterThan(0));
    }

    [Test]
    public void MissingFile_ReturnsNull()
    {
        RequireFfprobe();
        var kbps = MelodyBridge.Infrastructure.Audio.BitrateProbe.MeasureKbps(
            Path.Combine(_dir, "nope.flac"));
        Assert.That(kbps, Is.Null);
    }
}
