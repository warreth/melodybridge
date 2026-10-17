namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Synthetic Jumo stream fixtures, generated with the site's own client
/// so the box layout matches the live service exactly: an init segment
/// carrying the FLAC header and a two-row segment table, two media
/// segments with plain and AES-CTR encrypted frames plus trailing bytes,
/// and the exact plaintext outputs the assembly must produce.
/// </summary>
internal static class JumoFixtures
{
    public const string ContentKeyHex = "00112233445566778899aabbccddeeff";

    public const string InitBase64 =
        "AAAAFGZ0eXBtcDQybXA0MmlzbzYAAABxdXVpZMfHXfD92VHpj8IpceSs+NIAAAAAAIt3OAABPCIAAKxEEAIAAAAAAAAAAAAqZkxhQwAFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpAAACAAAAQgAAEgAAAAAjAAAkAAAAABhtb292AAAAAAAAAAAAAAAAAAAAAA==";

    public const string Media1Base64 =
        "AAAAGHN0eXAAAAAAAAAAAAAAAAAAAAAAAAAAVHV1aWQ7QhKSVvNfdZI2Y7aaH1KyAAAAAAAAAHQIAAADAAAAEhIAAAAAAAAAAAAAAAAAABYSAAABAAAAAAAAEAAAAAAVEgAAAQAAAAAAABABAAAAGG1vb2YAAAAAAAAAAAAAAAAAAAAAAAAASm1kYXRmcmFtZS1vbmUtcGxhaW4tYWGbQ+Osemjs7q6g0X6MBDawewNboDR1af3IbDSqFVMBVHXxRutm4YySJncvdGFpbDE=";

    public const string Media2Base64 =
        "AAAAGHN0eXAAAAAAAAAAAAAAAAAAAAAAAAAARHV1aWQ7QhKSVvNfdZI2Y7aaH1KyAAAAAAAAAGQIAAACAAAAEhIAAAAAAAAAAAAAAAAAABESAAABAAAAAAAAIAAAAAAYbW9vZgAAAAAAAAAAAAAAAAAAAAAAAAArbWRhdHNlZzItZnJhbWUtYS1wbGFpbkQnT30GHT1+NpmTNS+Ksl0O";

    public const string ExpectedFlacBase64 =
        "ZkxhQ4AFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpZnJhbWUtb25lLXBsYWluLWFhZnJhbWUtdHdvLWVuY3J5cHRlZC1iYmZyYW1lLXRocmVlLWNyeXB0ZWQtY3RhaWwxc2VnMi1mcmFtZS1hLXBsYWluc2VnMi1mcmFtZS1iLWVuY3I=";

    public const string ExpectedMp3Base64 =
        "ZnJhbWUtb25lLXBsYWluLWFhZnJhbWUtdHdvLWVuY3J5cHRlZC1iYmZyYW1lLXRocmVlLWNyeXB0ZWQtY3RhaWwxc2VnMi1mcmFtZS1hLXBsYWluc2VnMi1mcmFtZS1iLWVuY3I=";

    public static byte[] Init() => Convert.FromBase64String(InitBase64);

    public static List<byte[]> MediaSegments() =>
        [Convert.FromBase64String(Media1Base64), Convert.FromBase64String(Media2Base64)];

    public static byte[] ExpectedFlac() => Convert.FromBase64String(ExpectedFlacBase64);

    public static byte[] ExpectedMp3() => Convert.FromBase64String(ExpectedMp3Base64);

    public static byte[] ContentKey() => Convert.FromHexString(ContentKeyHex);

    /// <summary>The descriptor manifest pointing at a stub CDN, matching the fixtures.</summary>
    public static string DescriptorJson() => """
    {
      "qb": {
        "effective_format": "6",
        "n_segments": 2,
        "preview": false,
        "content_key": "00112233445566778899aabbccddeeff",
        "url_template": "https://cdn.melodybridge.test/seg?i=$SEGMENT$",
        "container": "flac"
      }
    }
    """;
}
