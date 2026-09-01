using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// TagLib round-trips on real generated audio files: an .opus (the remux
/// target for YouTube auto downloads) and an .mp3 (explicit transcode).
/// </summary>
[TestFixture]
[Category("Integration")]
public class TaglibHelperFormatTests
{
    private string _dir = null!;

    [OneTimeSetUp]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), $"mb-tag-{Guid.NewGuid():N}");

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Real opus file via ffmpeg's internal decoder — no network.</summary>
    private string MakeOpus()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "probe.opus");
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg")
            ?? throw new InvalidOperationException("ffmpeg required");
        var ok = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -v error -f lavfi -i anullsrc=r=44100:cl=mono -t 1 -c:a libopus -b:a 96k {path}",
            UseShellExecute = false,
        })!.WaitForExit(15000);
        Assert.That(ok && File.Exists(path), Is.True, "ffmpeg must produce the opus probe file");
        return path;
    }

    /// <summary>Real mp3 file via ffmpeg's internal encoder — no network.</summary>
    private string MakeMp3()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "probe.mp3");
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg")
            ?? throw new InvalidOperationException("ffmpeg required");
        var ok = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -v error -f lavfi -i anullsrc=r=44100:cl=mono -t 1 -c:a libmp3lame -b:a 128k {path}",
            UseShellExecute = false,
        })!.WaitForExit(15000);
        Assert.That(ok && File.Exists(path), Is.True, "ffmpeg must produce the mp3 probe file");
        return path;
    }

    [Test]
    public void OpusFile_MelodyIdRoundTrips()
    {
        var path = MakeOpus();
        TaglibHelper.WriteMelodyId(path, "mb-opus-1");
        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo("mb-opus-1"),
            "auto quality downloads are remuxed to opus; the id must survive");
    }

    [Test]
    public void Mp3File_MelodyIdRoundTrips()
    {
        var path = MakeMp3();
        TaglibHelper.WriteMelodyId(path, "mb-mp3-1");
        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo("mb-mp3-1"),
            "explicit format transcodes land in mp3; the id must survive");
    }

    [Test]
    public void OpusFile_TitleAndArtistRoundTrip()
    {
        var path = MakeOpus();
        TaglibHelper.WriteTags(path, title: "T", artist: "A");
        var read = TagLib.File.Create(path);
        Assert.That(read.Tag.Title, Is.EqualTo("T"));
        Assert.That(read.Tag.Performers, Does.Contain("A"));
    }
}
