using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Audio;

/// <summary>
/// The archive copy must never endanger the primary download. When the
/// archive directory is the download directory and the format is Auto,
/// the target path equals the primary path: File.Copy failed and the
/// failure cleanup deleted the target, which was the just-downloaded
/// file. The guard now refuses before touching anything.
/// </summary>
[TestFixture]
public class ArchiveCopierSafetyTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-archive-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A valid PCM WAV built from C# only: no external tools needed.</summary>
    private string MakeSilentWav(string name, int seconds = 1)
    {
        const int sampleRate = 44100;
        var dataBytes = sampleRate * seconds * 2;
        var path = Path.Combine(_dir, name);
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

    [Test]
    public void SameDirectoryAuto_KeepsThePrimaryUntouched()
    {
        var primary = MakeSilentWav("primary.wav");
        var before = File.ReadAllBytes(primary);

        var result = ArchiveCopier.Copy(primary, _dir, "auto",
            melodyId: null, title: null, artist: null, album: null, trackNumber: null);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(primary), Is.True,
                "the primary download must never be deleted by an archive misconfiguration");
            Assert.That(File.ReadAllBytes(primary), Is.EqualTo(before),
                "and its bytes must be untouched");
            Assert.That(result.Ok, Is.False,
                "copying a file onto itself cannot succeed");
            Assert.That(result.Reason, Is.Not.Null.And.Not.Empty,
                "the track warning must explain what to change");
        });
    }

    [Test]
    public void SameDirectoryWithAnotherFormat_StillTranscodes()
    {
        var ffmpeg = ArchiveCopier.FindFfmpeg();
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");
        if (SpectrumAnalyzer.FindFfprobe() is null) Assert.Ignore("ffprobe is not installed on this machine");

        var primary = MakeSilentWav("primary.wav");

        var result = ArchiveCopier.Copy(primary, _dir, "mp3",
            melodyId: "mel-same-dir", title: "Silence", artist: "No One",
            album: null, trackNumber: null,
            expectedDuration: TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, $"mp3 into the same directory is a different file: {result.Reason}");
            Assert.That(Path.GetExtension(result.ArchivePath), Is.EqualTo(".mp3"));
            Assert.That(File.Exists(primary), Is.True, "the primary stays");
            Assert.That(File.Exists(result.ArchivePath), Is.True);
        });
    }
}
