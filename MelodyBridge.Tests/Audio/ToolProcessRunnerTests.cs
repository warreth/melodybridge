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
    public void RunToStream_DeliversRawPcm()
    {
        var ffmpeg = Ffmpeg();
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");

        using var captured = new MemoryStream();
        var ok = ToolProcessRunner.RunToStream(ffmpeg, new[]
        {
            "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-ac", "1", "-ar", "44100", "-f", "s16le", "-",
        }, timeoutMs: 30000, stream => stream.CopyTo(captured));

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, "a clean exit must report success");
            // One second of mono 16-bit PCM at 44.1 kHz.
            Assert.That(captured.Length,
                Is.InRange(44100 * 2 - 4000, 44100 * 2 + 4000),
                "the decode must arrive as raw bytes");
            Assert.That(captured.ToArray(), Does.Contain((byte)0x00),
                "text decoding would have corrupted the PCM stream");
        });
    }

    [Test]
    public void RunToStream_HungTool_IsKilledWhenTheTimeoutExpires()
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
        var ok = ToolProcessRunner.RunToStream(ffmpeg, args, timeoutMs: 1500,
            stream => stream.CopyTo(Stream.Null));
        watch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False, "the timeout must report failure");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(30000),
                "the timeout must fire instead of waiting for the whole tool");
        });
    }
}
