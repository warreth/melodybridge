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

    [Test]
    public void MissingFfmpeg_LeavesAnExistingArchiveCopyAlone()
    {
        var primary = MakeSilentWav("primary.wav");
        var archiveDir = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(archiveDir);
        var target = Path.Combine(archiveDir, "primary.opus");
        var oldBytes = new byte[] { 7, 7, 7 };
        File.WriteAllBytes(target, oldBytes);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var emptyPath = Path.Combine(_dir, "empty-path");
        Directory.CreateDirectory(emptyPath);
        Environment.SetEnvironmentVariable("PATH", emptyPath);
        try
        {
            var result = ArchiveCopier.Copy(primary, archiveDir, "opus",
                melodyId: null, title: null, artist: null, album: null, trackNumber: null);

            Assert.Multiple(() =>
            {
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Reason, Does.Contain("ffmpeg"));
                Assert.That(File.Exists(target), Is.True,
                    "a rebuild that cannot run must not delete the old copy");
                Assert.That(File.ReadAllBytes(target), Is.EqualTo(oldBytes));
                Assert.That(Directory.EnumerateFiles(archiveDir, "*.mb-tmp-*"), Is.Empty,
                    "a failed attempt leaves no temp file behind");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Test]
    public void SameDirectory_DifferentCase_IsRefusedOnCaseInsensitiveHosts()
    {
        var primary = MakeSilentWav("primary.wav");
        var before = File.ReadAllBytes(primary);
        // The same folder with a differently cased leaf. On a
        // case-insensitive filesystem this is the primary's own path.
        var aliasDir = Path.Combine(
            Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir).ToUpperInvariant());

        var result = ArchiveCopier.Copy(primary, aliasDir, "auto",
            melodyId: null, title: null, artist: null, album: null, trackNumber: null);

        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Assert.Multiple(() =>
                {
                    Assert.That(result.Ok, Is.False,
                        "a case-only alias of the same folder is the primary's path");
                    Assert.That(File.ReadAllBytes(primary), Is.EqualTo(before),
                        "the primary's bytes stay untouched");
                });
            }
            else
            {
                Assert.That(result.Ok, Is.True,
                    "a case-sensitive host treats the alias as another folder");
            }
        }
        finally
        {
            // On a case-sensitive host the alias is a sibling directory
            // the fixture teardown does not know about.
            try
            {
                if (Directory.Exists(aliasDir)
                    && !string.Equals(
                        Path.GetFullPath(aliasDir), Path.GetFullPath(_dir),
                        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    Directory.Delete(aliasDir, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}
