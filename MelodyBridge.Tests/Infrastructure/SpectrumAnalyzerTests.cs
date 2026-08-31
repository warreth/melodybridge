using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Spectrum analyzer against real generated audio: a genuine high-bitrate
/// encode and an up-scaled re-encode of a low-bitrate source are built
/// with ffmpeg and must be told apart by the analyzer.
/// Skipped automatically when ffmpeg is not installed.
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
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            return p is not null && p.WaitForExit(5000);
        }
        catch { return false; }
    }

    /// <summary>Generates spectrally rich audio (white noise) as a base signal.</summary>
    private static string RunFfmpeg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return p.ExitCode == 0 ? string.Empty : $"ffmpeg exited {p.ExitCode}";
    }

    [Test]
    public void Verify_GenuineHighBitrate_NotFlagged()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var genuine = Path.Combine(_dir, "genuine.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=white:duration=20\" -b:a 320k \"{genuine}\""), Is.Empty);

        var result = SpectrumAnalyzer.Verify(genuine, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null, "analyzer must produce a result with ffmpeg present");
        Assert.That(result!.LooksInflated, Is.False,
            $"pink noise at 320 kbps is genuine; note: {result.Note}");
        Assert.That(result.EffectiveKbpsClass, Is.GreaterThanOrEqualTo(256));
    }

    [Test]
    public void Verify_UpscaledFromLowBitrate_FlaggedAsInflated()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        // 64 kbps base, re-encoded to 320: the classic blow-up file.
        var base64 = Path.Combine(_dir, "base64.mp3");
        var fake320 = Path.Combine(_dir, "fake320.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=white:duration=20\" -b:a 64k \"{base64}\""), Is.Empty);
        Assert.That(RunFfmpeg($"-y -i \"{base64}\" -b:a 320k \"{fake320}\""), Is.Empty);

        var result = SpectrumAnalyzer.Verify(fake320, SpectrumMode.Thorough);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LooksInflated, Is.True,
            "a 64 kbps source re-encoded to 320 kbps must be detected as inflated");
        Assert.That(result.EffectiveKbpsClass, Is.LessThanOrEqualTo(256),
            "the fake file must measure at least one class below genuine 320");
    }

    [Test]
    public void Verify_OffMode_ReturnsNull()
    {
        Assert.That(SpectrumAnalyzer.Verify("/whatever.mp3", SpectrumMode.Off), Is.Null);
    }

    [Test]
    public void Verify_MissingFile_ReturnsNullGracefully()
    {
        Assert.That(
            SpectrumAnalyzer.Verify(Path.Combine(_dir, "no-such-file.mp3"), SpectrumMode.Fast),
            Is.Null, "a missing file must fail open, not throw");
    }
}
