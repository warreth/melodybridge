using MelodyBridge.Infrastructure.Jumo;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Jumo container/decryption tests. The synthetic fixtures were generated
/// by the site's own client (validated byte-for-byte), so they exercise
/// the exact box layout the service produces: init uuid with a FLAC
/// header and segment table, media segments with a frame index and
/// AES-CTR encrypted mdat frames, plain frames and trailing bytes.
/// </summary>
[TestFixture]
public class JumoContainerTests
{
    // Known-answer vector for AES-CTR with a 64-bit block counter,
    // generated with WebCrypto (the scheme the site uses).
    private const string KatKey = "2b7e151628aed2a6abf7158809cf4f3c";
    private const string KatIv = "f0f1f2f3f4f5f6f7";
    private const string KatPlain = "4a756d6f20636f6e7461696e6572204145532d435452206b6e6f776e20616e7377657220766563746f722c203332206279746573";
    private const string KatCipher = "465ad6d97aba08446d9f945bfe816b43eafcb1b1be8b4d588ecab6b869e0debd5445d22ce26e222d9911e8f6bc5b6680a063cc07";

    private const string ContentKey = "00112233445566778899aabbccddeeff";

    private const string InitFixture =
        "AAAAFGZ0eXBtcDQybXA0MmlzbzYAAABxdXVpZMfHXfD92VHpj8IpceSs+NIAAAAAAIt3OAABPCIAAKxEEAIAAAAAAAAAAAAqZkxhQwAFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpAAACAAAAQgAAEgAAAAAjAAAkAAAAABhtb292AAAAAAAAAAAAAAAAAAAAAA==";

    private const string MediaSegment1 =
        "AAAAGHN0eXAAAAAAAAAAAAAAAAAAAAAAAAAAVHV1aWQ7QhKSVvNfdZI2Y7aaH1KyAAAAAAAAAHQIAAADAAAAEhIAAAAAAAAAAAAAAAAAABYSAAABAAAAAAAAEAAAAAAVEgAAAQAAAAAAABABAAAAGG1vb2YAAAAAAAAAAAAAAAAAAAAAAAAASm1kYXRmcmFtZS1vbmUtcGxhaW4tYWGbQ+Osemjs7q6g0X6MBDawewNboDR1af3IbDSqFVMBVHXxRutm4YySJncvdGFpbDE=";

    private const string MediaSegment2 =
        "AAAAGHN0eXAAAAAAAAAAAAAAAAAAAAAAAAAARHV1aWQ7QhKSVvNfdZI2Y7aaH1KyAAAAAAAAAGQIAAACAAAAEhIAAAAAAAAAAAAAAAAAABESAAABAAAAAAAAIAAAAAAYbW9vZgAAAAAAAAAAAAAAAAAAAAAAAAArbWRhdHNlZzItZnJhbWUtYS1wbGFpbkQnT30GHT1+NpmTNS+Ksl0O";

    private const string ExpectedFlac =
        "ZkxhQ4AFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpZnJhbWUtb25lLXBsYWluLWFhZnJhbWUtdHdvLWVuY3J5cHRlZC1iYmZyYW1lLXRocmVlLWNyeXB0ZWQtY3RhaWwxc2VnMi1mcmFtZS1hLXBsYWluc2VnMi1mcmFtZS1iLWVuY3I=";

    private const string ExpectedMp3 =
        "ZnJhbWUtb25lLXBsYWluLWFhZnJhbWUtdHdvLWVuY3J5cHRlZC1iYmZyYW1lLXRocmVlLWNyeXB0ZWQtY3RhaWwxc2VnMi1mcmFtZS1hLXBsYWluc2VnMi1mcmFtZS1iLWVuY3I=";

    // A real init segment captured from the service (format 6 stream of
    // "One More Time"); it anchors the parser to the live layout.
    private const string RealInitSegment =
        "AAAAGGZ0eXBtcDQyAAAAAG1wNDJpc282AAADTXV1aWTHx13w/dlR6Y/CKXHkrPjSAAAAAACLdzgAATwiAACsRBACAAAAAAC5T6gCJmZMYUMAAAAiEgASAAAF0wA8swrEQvAAuU+oBWFTDdXP5/t8SLO+bukeRYMAAfgAAAAAAAAAAAAAAAAAAAAAEgAAAAAAAAbAAAAAAAAAEUSfEgAAAAAAAA2AAAAAAAAAJFfYEgAAAAAAABRAAAAAAAAANSRtEgAAAAAAABruAAAAAAAARmTVEgAAAAAAACGuAAAAAAAAV69UEgAAAAAAAChuAAAAAAAAaaRlEgAAAAAAAC8cAAAAAAAAe9ATEgAAAAAAADXcAAAAAAAAjcUmEgAAAAAAADycAAAAAAAAn93aEgAAAAAAAENcAAAAAAAAseGsEgAAAAAAAEoKAAAAAAAAwv5LEgAAAAAAAFDKAAAAAAAA1XWnEgAAAAAAAFeKAAAAAAAA6MMEEgAAAAAAAF44AAAAAAAA+sAoEgAAAAAAAGT4AAAAAAABDQ7CEgAAAAAAAGu4AAAAAAABHmIIEgAAAAAAAHJmAAAAAAABMGLmEgAAAAAAAHkmAAAAAAABQtJgEgAAAAAAAH/mAAAAAAABVb4XEgAAAAAAAIamAAAAAAABaEriEgAAAAAAAI1UAAAAAAABeWVLEgAAAAAAAJQUAAAAAAABirrfEgAAAAAAAJrUAAAAAAABnGRZEgAAAAAAAKGCAAAAAAABrP2MEgAAAAAAAKhCAAAAAAABvibYEgAAAAAAAK8CAAAAAAABzgSCEgAAAAAAALWwAAAAAAAB2DkaEgAQeHO703XclvzFe6+roW1i8AAcABFEnwAGwAAAExM5AAbAAAAQzJUABsAAABFnVAAGwAAAEVWRAAbAAAAR9CAABsAAABJWLgAGwAAAEf5OAAbAAAASGPAABsAAABIDRwAGwAAAEUxKAAbAAAASdoAABsAAABNS1QAGwAAAEhsXAAbAAAASR78ABsAAABFfngAGwAAAEjVfAAbAAAASZFcABsAAABLy0gAGwAAAEnZ/AAbAAAARcykABsAAABFCwAAGwAAAEZo7AAbAAAAQxx8ABsAAABEZUAAGwAAAD9rLAAbAAAAJihwABsAAAAHuygADD6gAAAJTbW9vdgAAAGxtdmhkAAAAAAAAAAAAAAAAAAAD6AAEM7sAAQAAAQAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAad0cmFrAAAAXHRraGQAAAADAAAAAAAAAAAAAAABAAAAAAAEM7sAAAAAAAAAAAAAAAABAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAFDbWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAACsRAC5T6hVxAAAAAAALWhkbHIAAAAAAAAAAHNvdW4AAAAAAAAAAAAAAABTb3VuZEhhbmRsZXIAAAAA7m1pbmYAAAAQc21oZAAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAAsnN0YmwAAABmc3RzZAAAAAAAAAABAAAAVmZMYUMAAAAAAAAAAQAAAAAAAAAAAAIAEAAAAACsRAAAAAAAMmRmTGEAAAAAgAAAIhIAEgAABdMAPLMKxELwALlPqAVhUw3Vz+f7fEizvm7pHkUAAAAQc3R0cwAAAAAAAAAAAAAAEHN0c2MAAAAAAAAAAAAAABRzdHN6AAAAAAAAAAAAAAAAAAAAEHN0Y28AAAAAAAAAAAAAADhtdmV4AAAAEG1laGQAAAAAAAAAAAAAACB0cmV4AAAAAAAAAAEAAAABAAASAAAAAAACAAAA";

    private static byte[] Bytes(string base64) => Convert.FromBase64String(base64);

    private static JumoContainer.InitInfo InitFromFixture() => JumoContainer.ParseInit(Bytes(InitFixture));

    private static List<byte[]> MediaFromFixture() => [Bytes(MediaSegment1), Bytes(MediaSegment2)];

    [Test]
    public void DecryptFrame_KnownAnswerVector_MatchesWebCrypto()
    {
        var plain = JumoContainer.DecryptFrame(
            Convert.FromHexString(KatKey), Convert.FromHexString(KatIv), Convert.FromHexString(KatCipher));

        Assert.That(Convert.ToHexString(plain).ToLowerInvariant(), Is.EqualTo(KatPlain),
            "AES-CTR with a 64-bit counter must match the WebCrypto reference");
    }

    [Test]
    public void ParseInit_SyntheticFixture_ReadsHeaderAndTable()
    {
        var init = InitFromFixture();

        Assert.That(init.Kind, Is.EqualTo("flac"));
        Assert.That(init.Header.Length, Is.EqualTo(42));
        Assert.That(init.Header[0], Is.EqualTo((byte)'f'));
        Assert.That(init.Header[4] & 0x80, Is.EqualTo(0x80),
            "the STREAMINFO block must be marked as the last metadata block");
        Assert.That(init.Table.Count, Is.EqualTo(2));
        Assert.That(init.Table[0].ByteLength, Is.EqualTo(66)); // 18 + 22 + 21 frame bytes + 5 trailing bytes
        Assert.That(init.Table[1].ByteLength, Is.EqualTo(35));
        Assert.That(init.Table[0].SampleCount, Is.EqualTo(4608));
    }

    [Test]
    public void Assemble_SyntheticFixture_ProducesExactlyThePlainStream()
    {
        var init = InitFromFixture();
        var media = MediaFromFixture();

        var flac = JumoContainer.Assemble(init, media, Convert.FromHexString(ContentKey), "6");
        Assert.That(flac.Container, Is.EqualTo("flac"));
        Assert.That(flac.Bytes, Is.EqualTo(Bytes(ExpectedFlac)),
            "decrypted FLAC assembly (header, plain and encrypted frames, trailing bytes) must be exact");

        var mp3 = JumoContainer.Assemble(init, media, Convert.FromHexString(ContentKey), "5");
        Assert.That(mp3.Container, Is.EqualTo("mp3"));
        Assert.That(mp3.Bytes, Is.EqualTo(Bytes(ExpectedMp3)),
            "the MP3 path drops the header and keeps frames plus trailing bytes");
    }

    [Test]
    public void ParseSegment_SyntheticFixture_ReadsFrameIndex()
    {
        var layout = JumoContainer.ParseSegment(Bytes(MediaSegment1));

        Assert.That(layout.Frames.Count, Is.EqualTo(3));
        Assert.That(layout.Frames[0].Flags, Is.EqualTo(0));
        Assert.That(layout.Frames[1].Flags, Is.EqualTo(1));
        Assert.That(layout.Frames[2].Flags, Is.EqualTo(1));
        Assert.That(layout.Frames[0].Size, Is.EqualTo(18));
        Assert.That(layout.MdatEnd - layout.DataStart, Is.EqualTo(66));
    }

    [Test]
    public void Assemble_TruncatedSegment_Throws()
    {
        var init = InitFromFixture();
        var truncated = Bytes(MediaSegment1)[..^1];

        var ex = Assert.Throws<InvalidDataException>(() => JumoContainer.Assemble(
            init, [truncated, Bytes(MediaSegment2)], Convert.FromHexString(ContentKey), "6"));
        Assert.That(ex!.Message, Does.Contain("truncated"));
    }

    [Test]
    public void Assemble_SegmentCountMismatch_Throws()
    {
        var init = InitFromFixture();

        Assert.Throws<InvalidDataException>(() => JumoContainer.Assemble(
            init, [Bytes(MediaSegment1)], Convert.FromHexString(ContentKey), "6"));
    }

    [Test]
    public void ParseInit_RealServiceSegment_MatchesLiveLayout()
    {
        var init = JumoContainer.ParseInit(Bytes(RealInitSegment));

        Assert.That(init.Kind, Is.EqualTo("flac"));
        Assert.That(init.Header[..4], Is.EqualTo("fLaC"u8.ToArray()));
        Assert.That(init.Header[4] & 0x80, Is.EqualTo(0x80));
        Assert.That(init.Table.Count, Is.EqualTo(28), "One More Time has 28 FLAC segments");
        Assert.That(init.Table[0].ByteLength, Is.EqualTo(1131679u));
        Assert.That(init.Table[0].SampleCount, Is.EqualTo(442368u));
    }
}
