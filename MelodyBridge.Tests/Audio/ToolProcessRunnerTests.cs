using System.Diagnostics;
using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Audio;

/// <summary>
/// The shared tool runner: argument lists keep spaces intact, and the
/// timeout really kills a hung tool. The old pattern read the pipe
/// synchronously before WaitForExit, so a hung ffmpeg/ffprobe blocked
/// the caller forever and the timeout never fired.
/// </summary>
[TestFixture]
public class ToolProcessRunnerTests
{
    private static string? Ffmpeg()
        => SpectrumAnalyzer.FindBinary("ffmpeg");

    [Test]
    public void FastTool_ReturnsExitCodeAndDrainedOutput()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");

        var result = ToolProcessRunner.Run(ffmpeg, new[] { "-version" }, timeoutMs: 30000);

        Assert.Multiple(() =>
        {
            Assert.That(result.TimedOut, Is.False);
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(result.Stdout, Does.Contain("ffmpeg"),
                "the drained stdout must be returned");
        });
    }

    [Test]
    public void HungTool_IsKilledWhenTheTimeoutExpires()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");

        // An hour-long null encode: only the timeout can end it.
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3600",
            "-f", "null", "-",
        };
        var watch = Stopwatch.StartNew();
        var result = ToolProcessRunner.Run(ffmpeg, args, timeoutMs: 1500);
        watch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(result.TimedOut, Is.True, "the timeout must fire");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(30000),
                "a synchronous pipe read would hang until the tool finishes");
        });
    }

    [Test]
    public void RunBinary_ReturnsRawPcmBytes()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");

        var result = ToolProcessRunner.RunBinary(ffmpeg, new[]
        {
            "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-ac", "1", "-ar", "44100", "-f", "s16le", "-",
        }, timeoutMs: 30000);

        Assert.Multiple(() =>
        {
            Assert.That(result.TimedOut, Is.False);
            Assert.That(result.ExitCode, Is.EqualTo(0));
            // One second of mono 16-bit PCM at 44.1 kHz.
            Assert.That(result.Stdout.Length,
                Is.InRange(44100 * 2 - 4000, 44100 * 2 + 4000),
                "the decode must come back as raw bytes");
            Assert.That(result.Stdout, Does.Contain((byte)0x00),
                "text decoding would have corrupted the PCM stream");
        });
    }

    [Test]
    public void RunBinary_HungTool_IsKilledWhenTheTimeoutExpires()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");

        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3600",
            "-f", "s16le", "-",
        };
        var watch = Stopwatch.StartNew();
        var result = ToolProcessRunner.RunBinary(ffmpeg, args, timeoutMs: 1500);
        watch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(result.TimedOut, Is.True, "the timeout must fire");
            Assert.That(result.Stdout, Is.Empty);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(30000),
                "a synchronous pipe read would hang until the tool finishes");
        });
    }
}
