using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// ArchiveCopier against real files and a real ffmpeg: a valid WAV
/// written entirely from C# (RIFF header plus seconds of silence)
/// copied byte-for-byte for format auto, transcoded to opus and mp3
/// with the MELODY_ID handover and the ffprobe integrity gate, and the
/// failure isolation when the primary path does not exist. Tests that
/// transcode skip honestly on hosts without ffmpeg or ffprobe.
/// </summary>
[TestFixture]
[Category("Live")]
public class ArchiveCopierTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A valid PCM WAV built from C# only: the 44-byte RIFF header plus
    /// seconds of 16-bit mono silence at 44.1 kHz. ffmpeg and TagLib
    /// both accept it as a genuine audio file.
    /// </summary>
    private string MakeSilentWav(int seconds = 2)
    {
        const int sampleRate = 44100;
        var dataBytes = sampleRate * seconds * 2;
        var path = Path.Combine(_dir, $"primary-{Guid.NewGuid():N}.wav");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return path;
    }

    /// <summary>An archive directory that does not exist yet: Copy must create it.</summary>
    private string FreshArchiveDir() => Path.Combine(_dir, $"archive-{Guid.NewGuid():N}");

    /// <summary>Transcodes need ffmpeg, the integrity probe needs ffprobe.</summary>
    private static bool MediaToolsAvailable()
        => ArchiveCopier.FindFfmpeg() is not null
            && SpectrumAnalyzer.FindFfprobe() is not null;

    [Test]
    public void AutoCopy_WritesIdenticalBytesWithSameExtension()
    {
        var primary = MakeSilentWav();
        var archiveDir = FreshArchiveDir();

        var result = ArchiveCopier.Copy(primary, archiveDir, "auto",
            melodyId: null, title: null, artist: null, album: null, trackNumber: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, $"auto copy must succeed; reason: {result.Reason}");
            Assert.That(result.Reason, Is.Null, "a success carries no reason");
            Assert.That(File.Exists(result.ArchivePath), Is.True,
                "the archive copy must exist on disk");
            Assert.That(Path.GetExtension(result.ArchivePath), Is.EqualTo(".wav"),
                "auto keeps the primary's extension");
            Assert.That(Path.GetDirectoryName(result.ArchivePath),
                Is.EqualTo(Path.GetFullPath(archiveDir)),
                "the copy lands inside the archive directory");
            Assert.That(File.ReadAllBytes(result.ArchivePath),
                Is.EqualTo(File.ReadAllBytes(primary)),
                "auto is a byte-for-byte copy, no re-encode");
        });
    }

    [Test]
    public void OpusCopy_TranscodesTagsAndPassesIntegrity()
    {
        if (!MediaToolsAvailable()) Assert.Ignore("ffmpeg or ffprobe not installed on this host");

        var primary = MakeSilentWav();
        var archiveDir = FreshArchiveDir();

        var result = ArchiveCopier.Copy(primary, archiveDir, "opus",
            melodyId: "mel-opus-1", title: "Silence", artist: "No One",
            album: "Quiet", trackNumber: 1,
            expectedDuration: TimeSpan.FromSeconds(2));

        Assert.That(result.Ok, Is.True, $"opus transcode must succeed; reason: {result.Reason}");
        Assert.That(Path.GetExtension(result.ArchivePath), Is.EqualTo(".opus"),
            "the archive format decides the extension");

        // The source WAV carries no tags, so ffmpeg dropped the MELODY_ID
        // and the handover must have rewritten the full tag set.
        var tags = TagLib.File.Create(result.ArchivePath);
        Assert.That(tags.Tag.Title, Is.EqualTo("Silence"),
            "the melodyId handover rewrites the standard tags too");
        Assert.That(TaglibHelper.ReadMelodyId(result.ArchivePath), Is.EqualTo("mel-opus-1"),
            "the archive copy joins reconciliation on MELODY_ID");

        var integrity = FileIntegrity.Check(result.ArchivePath, TimeSpan.FromSeconds(2));
        Assert.That(integrity.Ok, Is.True,
            $"ffprobe must parse the archive copy; reason: {integrity.Reason}");
    }

    [Test]
    public void Mp3Copy_TranscodesToMp3AndParses()
    {
        if (!MediaToolsAvailable()) Assert.Ignore("ffmpeg or ffprobe not installed on this host");

        var primary = MakeSilentWav();

        var result = ArchiveCopier.Copy(primary, FreshArchiveDir(), "mp3",
            melodyId: "mel-mp3-1", title: "Silence", artist: "No One",
            album: null, trackNumber: null,
            expectedDuration: TimeSpan.FromSeconds(2));

        Assert.That(result.Ok, Is.True, $"mp3 transcode must succeed; reason: {result.Reason}");
        Assert.That(Path.GetExtension(result.ArchivePath), Is.EqualTo(".mp3"));
        Assert.That(new FileInfo(result.ArchivePath).Length, Is.GreaterThan(0),
            "the copy must be a real file, not a zero-byte stub");

        var integrity = FileIntegrity.Check(result.ArchivePath, TimeSpan.FromSeconds(2));
        Assert.That(integrity.Ok, Is.True,
            $"the mp3 archive copy must be parseable audio; reason: {integrity.Reason}");
    }

    [Test]
    public void MissingPrimary_ReturnsFailureInsteadOfThrowing()
    {
        var archiveDir = FreshArchiveDir();
        var missing = Path.Combine(_dir, "never-existed.wav");

        // No assert.Throws wrapper: the contract is that nothing escapes.
        var result = ArchiveCopier.Copy(missing, archiveDir, "auto",
            melodyId: null, title: null, artist: null, album: null, trackNumber: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False, "a missing primary must not report success");
            Assert.That(result.Reason, Is.Not.Null.And.Not.Empty,
                "the failure carries a terse reason for the track warning");
            Assert.That(File.Exists(Path.Combine(archiveDir, "never-existed.wav")), Is.False,
                "nothing is left behind in the archive directory");
        });
    }
}
