using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Magic-byte sniffing: every container the downloaders can produce must
/// be recognized, and error pages / JSON bodies must never pass as audio.
/// The sniff reads real bytes, so a file written with the wrong extension
/// still lands as its true container.
/// </summary>
[TestFixture]
public class AudioSignatureTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-sig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteBytes(params byte[] bytes)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Test]
    public void Mp3_WithId3Tag_IsDetected()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes(0x49, 0x44, 0x33, 0x03)), Is.EqualTo(".mp3"));

    [Test]
    public void Mp3_WithFrameSync_IsDetected()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes(0xFF, 0xFB, 0x90, 0x00)), Is.EqualTo(".mp3"));

    [Test]
    public void Flac_IsDetected()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes(0x66, 0x4C, 0x61, 0x43, 0x00, 0x00)), Is.EqualTo(".flac"));

    [Test]
    public void M4a_IsDetected()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes(0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41)), Is.EqualTo(".m4a"));

    [Test]
    public void Opus_IsDetected_FromTheOggPage()
    {
        var bytes = new List<byte> { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02 };
        bytes.AddRange(new byte[22]);
        bytes.AddRange("OpusHead"u8.ToArray());
        Assert.That(AudioSignature.DetectContainer(WriteBytes(bytes.ToArray())), Is.EqualTo(".opus"));
    }

    [Test]
    public void OggVorbis_IsDetected()
    {
        var bytes = new List<byte> { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02 };
        bytes.AddRange(new byte[22]);
        bytes.AddRange("vorbis"u8.ToArray());
        Assert.That(AudioSignature.DetectContainer(WriteBytes(bytes.ToArray())), Is.EqualTo(".ogg"));
    }

    [Test]
    public void Wav_IsDetected()
    {
        var bytes = new List<byte> { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' };
        Assert.That(AudioSignature.DetectContainer(WriteBytes(bytes.ToArray())), Is.EqualTo(".wav"));
    }

    [Test]
    public void Webm_IsDetected()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes(0x1A, 0x45, 0xDF, 0xA3, 0x01)), Is.EqualTo(".webm"));

    [Test]
    public void Html_IsNotAudio()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes("<html><body>403</body></html>"u8.ToArray())), Is.Null);

    [Test]
    public void Json_IsNotAudio()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes("{\"detail\":\"Upstream API error\"}"u8.ToArray())), Is.Null);

    [Test]
    public void EmptyFile_IsNotAudio()
        => Assert.That(AudioSignature.DetectContainer(WriteBytes()), Is.Null);

    [Test]
    public void EnsureExtension_RenamesToTheSniffedContainer()
    {
        // FLAC bytes saved under an .mp3 name (a mirror served it with the
        // wrong content type): the rename follows the bytes.
        var path = Path.Combine(_dir, "track.mp3");
        File.WriteAllBytes(path, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22 });

        var result = AudioSignature.EnsureExtension(path);

        Assert.That(result, Is.EqualTo(Path.Combine(_dir, "track.flac")));
        Assert.That(File.Exists(result), Is.True);
        Assert.That(File.Exists(path), Is.False, "the mislabeled name must be gone");
    }

    [Test]
    public void EnsureExtension_NonAudio_ReturnsNull()
    {
        var path = Path.Combine(_dir, "track.mp3");
        File.WriteAllText(path, "not audio at all");
        Assert.That(AudioSignature.EnsureExtension(path), Is.Null);
    }
}
