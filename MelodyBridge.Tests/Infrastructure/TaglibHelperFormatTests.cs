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

    /// <summary>Real opus file via ffmpeg's internal decoder: no network.</summary>
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

    /// <summary>Real mp3 file via ffmpeg's internal encoder: no network.</summary>
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

    [TestCase("mp3")]
    [TestCase("opus")]
    public void MelodyId_SurvivesTheTagRewrite(string kind)
    {
        // The download manager stamps the id first; the pipeline then
        // writes title, artist, album, cover and lyrics over the same file.
        // The id is the scanner's only handle, so it must survive.
        var path = kind == "mp3" ? MakeMp3() : MakeOpus();
        TaglibHelper.WriteMelodyId(path, "mb-keep");

        TaglibHelper.WriteTags(path,
            title: "Title", artist: "Artist", album: "Album",
            albumArtist: "Various Artists", track: 3, lyrics: "words");

        Assert.That(TaglibHelper.ReadMelodyId(path), Is.EqualTo("mb-keep"),
            "a tag rewrite after the id must not drop the scanner's identity");
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

    /// <summary>A real 1x1 PNG: the smallest byte set TagLib accepts as a picture.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Test]
    public void Mp3File_AlbumArtistCoverAndLyricsRoundTrip()
    {
        var path = MakeMp3();
        var cover = TinyPng();

        TaglibHelper.WriteTags(path,
            title: "Title", artist: "Artist", album: "Album",
            albumArtist: "Various Artists", track: 3,
            coverArt: cover, lyrics: "first line\nsecond line");

        var read = TagLib.File.Create(path);
        Assert.Multiple(() =>
        {
            Assert.That(read.Tag.AlbumArtists, Does.Contain("Various Artists"),
                "the release credit is what media servers group on");
            Assert.That(read.Tag.Pictures, Has.Length.EqualTo(1));
            Assert.That(read.Tag.Pictures[0].Data.Data, Is.EqualTo(cover),
                "the embedded bytes must come back unchanged");
            Assert.That(read.Tag.Lyrics, Does.Contain("first line"));
        });
    }

    [Test]
    public void OpusFile_AlbumArtistCoverAndLyricsRoundTrip()
    {
        var path = MakeOpus();
        var cover = TinyPng();

        TaglibHelper.WriteTags(path,
            title: "Title", artist: "Artist", album: "Album",
            albumArtist: "Various Artists", coverArt: cover, lyrics: "words");

        var read = TagLib.File.Create(path);
        Assert.Multiple(() =>
        {
            Assert.That(read.Tag.AlbumArtists, Does.Contain("Various Artists"));
            Assert.That(read.Tag.Pictures, Has.Length.EqualTo(1));
            Assert.That(read.Tag.Lyrics, Does.Contain("words"));
        });
    }

    [Test]
    public void HasPictures_TracksWhetherCoverArtIsEmbedded()
    {
        var path = MakeMp3();
        Assert.That(TaglibHelper.HasPictures(path), Is.False, "a fresh file has no art");

        TaglibHelper.WriteTags(path, coverArt: TinyPng());
        Assert.That(TaglibHelper.HasPictures(path), Is.True,
            "an embedded cover must be visible to the pipeline before it fetches another");
    }
}
