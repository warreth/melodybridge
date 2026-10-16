using System.Diagnostics;
using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The spectrum check against real ffmpeg audio, built for the shapes it
/// must tell apart. Real music is bass-heavy, so the old rolloff
/// percentile read 6-10 kHz on genuine files and flagged them; the check
/// now measures the real bandwidth from a decoded spectrum.
///
/// The corpus pins the whole contract:
///   - every genuine encode, at every bitrate and codec, is never
///     inflated (the regression that failed the release gate);
///   - a low-bitrate source re-encoded at 320 is inflated;
///   - content that is band-limited rather than mislabelled is not.
/// Skipped honestly when ffmpeg is not installed.
/// </summary>
[TestFixture]
[Category("Spectrum")]
public class SpectrumAnalyzerTests
{
    private string _dir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-spec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            return p is not null && p.WaitForExit(5000);
        }
        catch { return false; }
    }

    private static void RequireFfmpeg()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");
    }

    /// <summary>Makes one audio file and returns its path.</summary>
    private string Make(string args, string name)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -v error {args} \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        Assert.That(p.ExitCode, Is.EqualTo(0), $"ffmpeg failed for {name}: {stderr}");
        return path;
    }

    /// <summary>Re-encodes an existing file (the upscale step).</summary>
    private string From(string input, string args, string name)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -v error -i \"{input}\" {args} \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        Assert.That(p.ExitCode, Is.EqualTo(0), $"ffmpeg failed for {name}: {stderr}");
        return path;
    }

    private const string Pink = "anoisesrc=color=pink:duration=4:seed=4";
    private const string White = "anoisesrc=color=white:duration=4:seed=4";

    // ---- genuine files: the regression the release gate found ----------

    [TestCase(64)]
    [TestCase(96)]
    [TestCase(128)]
    [TestCase(160)]
    [TestCase(192)]
    [TestCase(256)]
    [TestCase(320)]
    public void Verify_GenuineMp3_IsNeverInflated(int bitrate)
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{Pink}\" -b:a {bitrate}k", $"g{bitrate}.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False,
            $"a genuine {bitrate} kbps encode must not be called inflated: {result.Note}");
        Assert.That(result.Codec, Is.EqualTo("mp3"));
        Assert.That(result.DeclaredKbps, Is.EqualTo(bitrate).Within(2));
    }

    [TestCase(96)]
    [TestCase(128)]
    [TestCase(320)]
    public void Verify_GenuineAac_IsNeverInflated(int bitrate)
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{Pink}\" -c:a aac -b:a {bitrate}k", $"ga{bitrate}.m4a");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False,
            $"a genuine {bitrate} kbps AAC must not be called inflated: {result.Note}");
        Assert.That(result.Codec, Is.EqualTo("aac"));
    }

    [Test]
    public void Verify_GenuineOpus_IsNeverInflated()
    {
        RequireFfmpeg();
        // Opus stays near-fullband at low bitrates: its bandwidth must
        // never be read as a low bitrate.
        var file = Make($"-f lavfi -i \"{Pink}\" -c:a libopus -b:a 96k", "g96.opus");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Codec, Is.EqualTo("opus"));
        Assert.That(result.LooksInflated, Is.False);
        Assert.That(result.EffectiveKbpsClass, Is.EqualTo(320),
            "Opus bandwidth must not imply a low class");
    }

    [Test]
    public void Verify_GenuineWhiteNoise320_IsNotInflated()
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{White}\" -b:a 320k", "gwhite320.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False);
        Assert.That(result.EffectiveKbpsClass, Is.EqualTo(320));
    }

    [Test]
    public void Verify_GenuineFullBand_HasFullBandwidth()
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{Pink}\" -b:a 320k", "gbw320.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BandwidthHz, Is.GreaterThan(18000),
            $"a genuine 320 encode keeps its high end: {result.Note}");
        Assert.That(result.BandwidthHz, Is.LessThan(22000));
    }

    [Test]
    public void Verify_GenuineMp3_48k_IsNotInflated()
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{Pink}\" -ar 48000 -b:a 320k", "g48.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False);
        Assert.That(result.EffectiveKbpsClass, Is.EqualTo(320));
    }

    // ---- mislabelled files: the case the check exists for --------------

    [Test]
    public void Verify_Upscaled64Mp3To320_IsInflated()
    {
        RequireFfmpeg();
        var source = Make($"-f lavfi -i \"{Pink}\" -b:a 64k", "src64.mp3");
        var file = From(source, "-b:a 320k", "up64.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.True,
            $"a 64 kbps source re-encoded at 320 must be flagged: {result.Note}");
        Assert.That(result.CodecCutoff, Is.True, "the 64 kbps lowpass is a real corner");
        Assert.That(result.DeclaredKbps, Is.EqualTo(320).Within(2));
        // The flagged class is 192, so the real boundary is under 17.5 kHz.
        Assert.That(result.BandwidthHz, Is.LessThan(17500));
        Assert.That(result.Note, Does.Contain("bandwidth stops"));
    }

    [Test]
    public void Verify_Upscaled64WhiteTo320_IsInflated()
    {
        RequireFfmpeg();
        var source = Make($"-f lavfi -i \"{White}\" -b:a 64k", "srcw64.mp3");
        var file = From(source, "-b:a 320k", "upw64.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.True);
    }

    [Test]
    public void Verify_Upscaled_FlaggedInBothModes()
    {
        RequireFfmpeg();
        var source = Make($"-f lavfi -i \"{Pink}\" -b:a 64k", "srcboth.mp3");
        var file = From(source, "-b:a 320k", "upboth.mp3");

        Assert.That(SpectrumAnalyzer.Verify(file, SpectrumMode.Fast)!.LooksInflated, Is.True,
            "the first minute is enough to see a 64 kbps corner");
        Assert.That(SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough)!.LooksInflated, Is.True);
    }

    // ---- band-limited content: honest, not mislabelled -----------------

    [Test]
    public void Verify_MildLowpass_StaysAtFullClass()
    {
        RequireFfmpeg();
        // A one-pole lowpass barely moves the measured high end: the codec's
        // own corner near 20 kHz still dominates, so the file stays class 320
        // and is not inflated. The gentle-fade case, where the measured high
        // end really is low, is covered by
        // Verify_MusicLikeSoftHighEnd_HasNoCodecCutoff.
        var file = Make($"-f lavfi -i \"{Pink}\" -af lowpass=f=11000:poles=1 -b:a 320k", "mild-lp.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EffectiveKbpsClass, Is.EqualTo(320),
            "a mild lowpass must not drag the measured class down");
        Assert.That(result.LooksInflated, Is.False);
    }

    [Test]
    public void Verify_MusicLikeSoftHighEnd_HasNoCodecCutoff()
    {
        RequireFfmpeg();
        // A treble shelf takes the top down like a dark mix: the measured
        // high end sits low, but the drop is a fade, not a codec corner.
        // This is the shape that must never be called inflated, and it is
        // the only corpus case where the cliff guard is load-bearing: the
        // bandwidth alone would imply 96 kbps against 320 declared.
        var file = Make($"-f lavfi -i \"{Pink}\" -af treble=g=-20:f=4000 -b:a 320k", "soft-top.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BandwidthHz, Is.LessThan(9000),
            "the measured high end is low, so the bandwidth rule alone cannot spare it");
        Assert.That(result.CodecCutoff, Is.False, "a treble shelf is not a codec cutoff");
        Assert.That(result.LooksInflated, Is.False,
            $"a dark mix is not a mislabelled rip: {result.Note}");
    }

    [Test]
    public void Verify_BandLimited15k_IsNotInflated()
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{Pink}\" -af lowpass=f=15000 -b:a 320k", "bl15.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False,
            $"a 15 kHz corner is inside the one-class tolerance: {result.Note}");
    }

    [Test]
    public void Verify_LowTonesOnly_IsNotInflated()
    {
        RequireFfmpeg();
        var file = Make(
            "-f lavfi -i \"sine=frequency=120:duration=4\" -f lavfi -i \"sine=frequency=250:duration=4\" " +
            "-filter_complex amix=inputs=2 -b:a 320k", "tones.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False, "bass-only content is not a mislabelled rip");
    }

    // ---- degenerate inputs ---------------------------------------------

    [Test]
    public void Verify_Silence_ReturnsNull()
    {
        RequireFfmpeg();
        var file = Make("-f lavfi -i \"anullsrc=r=44100:cl=mono:d=4\" -b:a 320k", "silence.mp3");

        Assert.That(SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough), Is.Null,
            "digital silence has no spectrum to judge");
    }

    [Test]
    public void Verify_VeryShortFile_DoesNotThrow()
    {
        RequireFfmpeg();
        var file = Make($"-f lavfi -i \"{Pink.Replace("duration=4", "duration=1")}\" -b:a 320k", "short.mp3");

        var result = SpectrumAnalyzer.Verify(file, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.False);
    }

    [Test]
    public void Verify_OffMode_ReturnsNull()
    {
        Assert.That(SpectrumAnalyzer.Verify("/whatever.mp3", SpectrumMode.Off), Is.Null);
    }

    [Test]
    public void Verify_MissingFile_ReturnsNullGracefully()
    {
        RequireFfmpeg();
        Assert.That(
            SpectrumAnalyzer.Verify(Path.Combine(_dir, "no-such-file.mp3"), SpectrumMode.Fast),
            Is.Null, "a missing file must fail open, not throw");
    }

    [Test]
    public void Verify_NotAudio_ReturnsNullGracefully()
    {
        var text = Path.Combine(_dir, "not-audio.mp3");
        File.WriteAllText(text, "this is not an audio file at all");
        Assert.That(SpectrumAnalyzer.Verify(text, SpectrumMode.Fast), Is.Null,
            "a non-audio file must fail open, not throw");
    }
}
