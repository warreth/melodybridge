using System.Diagnostics;
using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// WriteMelodyId/ReadMelodyId round-trips on real ffmpeg-generated audio
/// files across formats: MP3 (ID3v2 TXXX), FLAC and Ogg Opus (Xiph field),
/// M4A/AAC (comment marker fallback). Every format is also written twice
/// with different ids to prove idempotency: no duplicate marker, the old
/// id is never returned.
/// </summary>
[TestFixture]
[Category("Integration")]
public class TaglibMelodyIdFormatsTests
{
    private string _dir = null!;

    [OneTimeSetUp]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), $"mb-mid-{Guid.NewGuid():N}");

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Real audio via ffmpeg's internal generators: no network.</summary>
    private string Make(string ext, string codec, string bitrate = "96k")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"probe-{Guid.NewGuid():N}{ext}");
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg")
            ?? throw new InvalidOperationException("ffmpeg required");
        var ok = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -v error -f lavfi -i anullsrc=r=44100:cl=mono -t 1 -c:a {codec} -b:a {bitrate} {path}",
            UseShellExecute = false,
        })!.WaitForExit(15000);
        Assert.That(ok && File.Exists(path), Is.True, $"ffmpeg must produce the {ext} probe file");
        return path;
    }

    private string MakeMp3() => Make(".mp3", "libmp3lame", "128k");
    private string MakeFlac() => Make(".flac", "flac");
    private string MakeOpus() => Make(".opus", "libopus");
    private string MakeM4a() => Make(".m4a", "aac");

    private static void AssertRoundTrip(string path, string first, string second, string why)
    {
        TaglibHelper.WriteMelodyId(path, first);
        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo(first), why);
        TaglibHelper.WriteMelodyId(path, second);
        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo(second),
            why + " (rewrite must replace the old id, not duplicate the marker)");
    }

    private static void AssertXiphField(string path, string expected)
    {
        var xiph = TagLib.File.Create(path).GetTag(TagLib.TagTypes.Xiph) as TagLib.Ogg.XiphComment;
        Assert.That(xiph?.GetFirstField("MELODY_ID"), Is.EqualTo(expected),
            "FLAC/Opus ids must be stored as the MELODY_ID Xiph comment field");
    }

    [Test]
    public void Mp3_RoundTripsAndRewrites()
        => AssertRoundTrip(MakeMp3(), "mb-mp3-1", "mb-mp3-2",
            "explicit format transcodes land in mp3; the id must survive");

    [Test]
    public void Flac_RoundTripsAndRewrites()
        => AssertRoundTrip(MakeFlac(), "mb-flac-1", "mb-flac-2",
            "flac ids must survive");

    [Test]
    public void Flac_UsesXiphField()
    {
        var path = MakeFlac();
        TaglibHelper.WriteMelodyId(path, "mb-flac-x");
        AssertXiphField(path, "mb-flac-x");
        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo("mb-flac-x"));
    }

    [Test]
    public void Opus_RoundTripsAndRewrites()
        => AssertRoundTrip(MakeOpus(), "mb-opus-1", "mb-opus-2",
            "auto quality downloads are remuxed to opus; the id must survive");

    [Test]
    public void Opus_UsesXiphField()
    {
        var path = MakeOpus();
        TaglibHelper.WriteMelodyId(path, "mb-opus-x");
        AssertXiphField(path, "mb-opus-x");
        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo("mb-opus-x"));
    }

    [Test]
    public void M4a_RoundTripsAndRewrites()
        => AssertRoundTrip(MakeM4a(), "mb-m4a-1", "mb-m4a-2",
            "m4a ids must survive through the comment marker fallback");

    [Test]
    public void M4a_ExistingCommentMarker_IsReplacedNotAppended()
    {
        var path = MakeM4a();

        // Seed a user comment that already carries a MELODY_ID marker.
        var file = TagLib.File.Create(path);
        file.Tag.Comment = "liner notes from the source\nMELODY_ID=old";
        file.Save();

        TaglibHelper.WriteMelodyId(path, "mb-m4a-new");
        var after = TagLib.File.Create(path);

        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo("mb-m4a-new"),
            "the new id must win over the pre-existing marker");
        var comment = after.Tag.Comment ?? string.Empty;
        Assert.That(comment, Does.Not.Contain("MELODY_ID=old"),
            "the old marker must be replaced, not kept alongside the new one");
        Assert.That(comment.Split("MELODY_ID=").Length - 1, Is.EqualTo(1),
            "rewriting must not append a second MELODY_ID marker");
    }
}
