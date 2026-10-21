using System.Diagnostics;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// A webm download is remuxed to opus before tagging. The test encodes a
/// real one-second webm with ffmpeg, remuxes it through the production
/// helper, and proves the MELODY_ID survives in the new container. That is
/// the whole reason for the remux: webm tags are not writable.
/// </summary>
[TestFixture]
public class WebmRemuxTests
{
    [Test]
    public void Remux_RecontainersWebmAndKeepsMelodyId()
    {
        var ffmpeg = SpectrumAnalyzer.FindBinary("ffmpeg");
        Assert.That(ffmpeg, Is.Not.Null, "ffmpeg is required for a real remux");

        var dir = Path.Combine(Path.GetTempPath(), $"mb-remux-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var webm = Path.Combine(dir, "source.webm");
            var info = new ProcessStartInfo(ffmpeg!)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-y");
            info.ArgumentList.Add("-v");
            info.ArgumentList.Add("error");
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add("lavfi");
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add("anullsrc=r=48000:cl=mono");
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add("1");
            info.ArgumentList.Add("-c:a");
            info.ArgumentList.Add("libopus");
            info.ArgumentList.Add(webm);

            using (var encode = Process.Start(info)!)
            {
                encode.WaitForExit(30000);
                Assert.That(encode.ExitCode, Is.Zero, "the fixture webm must encode");
            }
            Assert.That(new FileInfo(webm).Length, Is.GreaterThan(0));

            var opus = YtDlpDownloader.RemuxWebmToOpus(webm);

            Assert.Multiple(() =>
            {
                Assert.That(opus, Is.Not.Null, "ffmpeg is present and the stream is opus");
                Assert.That(Path.GetExtension(opus), Is.EqualTo(".opus"));
                Assert.That(File.Exists(opus), Is.True);
                Assert.That(File.Exists(webm), Is.False,
                    "the untaggable original is removed");
            });

            TaglibHelper.WriteMelodyId(opus!, "mb-remux");
            Assert.That(TaglibHelper.ReadMelodyId(opus!), Is.EqualTo("mb-remux"),
                "the scanner's identity fits in the new container");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public void Remux_LeavesAnotherContainerAlone()
    {
        Assert.That(YtDlpDownloader.RemuxWebmToOpus("/tmp/song.mp3"), Is.Null);
        Assert.That(File.Exists("/tmp/song.mp3"), Is.False);
    }
}
