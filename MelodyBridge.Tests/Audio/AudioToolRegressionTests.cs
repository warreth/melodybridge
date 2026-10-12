using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Audio;

/// <summary>
/// Tool-path regressions: a music library under a path with a space
/// must not fail the audio probes. The space case used to fail because
/// the path was spliced into a raw Arguments string and ffprobe read it
/// as several arguments, so every download under such a path was
/// deleted as "corrupt".
/// </summary>
[TestFixture]
public class AudioToolRegressionTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        // The space is the point of this fixture.
        _dir = Path.Combine(Path.GetTempPath(), $"mb tool {Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void RequireFfprobe()
    {
        if (SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffprobe is not installed on this machine");
    }

    /// <summary>
    /// A valid PCM WAV built from C# only (no ffmpeg needed to create
    /// it): the 44-byte RIFF header plus seconds of 16-bit mono silence.
    /// ffprobe parses it like any other audio file.
    /// </summary>
    private string MakeSilentWav(string name, int seconds = 2)
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
    public void BitrateProbe_ReadsAPathWithSpaces()
    {
        RequireFfprobe();
        var path = MakeSilentWav("my song.wav");

        var kbps = BitrateProbe.MeasureKbps(path);

        Assert.That(kbps, Is.Not.Null.And.GreaterThan(0),
            "ffprobe must receive the path as one argument, spaces and all");
    }

    [Test]
    public void FileIntegrity_AcceptsAPathWithSpaces()
    {
        RequireFfprobe();
        var path = MakeSilentWav("my song.wav");

        var result = FileIntegrity.Check(path);

        Assert.That(result.Ok, Is.True,
            $"a valid file under a path with spaces is not corrupt; reason: {result.Reason}");
    }
}
