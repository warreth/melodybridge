namespace MelodyBridge.Tests;

/// <summary>
/// A minimal but valid MP3 (empty ID3v2.3 header + silence frames,
/// MPEG-1 Layer III 128 kbps 44.1 kHz) for downloader stubs. The
/// download pipeline ffprobes every finished file, so test files must
/// be real audio or they fail the integrity gate.
/// </summary>
public static class TestAudio
{
    public static byte[] MinimalMp3()
    {
        var id3Header = new byte[]
        {
            0x49, 0x44, 0x33, 0x03, 0x00, 0x00, // "ID3" v2.3, no flags
            0x00, 0x00, 0x00, 0x00,             // tag size = 0 (syncsafe)
        };
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;

        var fileBytes = new byte[id3Header.Length + frame.Length * 40];
        id3Header.CopyTo(fileBytes, 0);
        for (var i = 0; i < 40; i++)
            frame.CopyTo(fileBytes, id3Header.Length + frame.Length * i);
        return fileBytes;
    }
}
