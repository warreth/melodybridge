using System.Text;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Helpers for the media-server live tests: tagged MP3s written without any
/// external tool (an ID3v2.3 tag in front of TestAudio's valid frames) and
/// docker process plumbing shared by the server fixtures.
/// </summary>
public static class LiveFixtureHelpers
{
    /// <summary>Writes a valid MP3 whose ID3 tag carries the given title/artist,
    /// so real library scanners pick up real metadata.</summary>
    public static void WriteTaggedMp3(string path, string title, string artist)
    {
        File.WriteAllBytes(path, TaggedMp3(title, artist));
    }

    /// <summary>TestAudio frames with a TIT2/TPE1 ID3v2.3 tag prepended.</summary>
    public static byte[] TaggedMp3(string title, string artist)
    {
        byte[] Frame(string id, string text)
        {
            var body = new byte[] { 0x00 }.Concat(Encoding.UTF8.GetBytes(text)).ToArray(); // encoding 0 = ISO-8859-1-ish
            var size = BitConverter.GetBytes(body.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(size);
            return Encoding.ASCII.GetBytes(id).Concat(size)
                .Concat(new byte[] { 0x00, 0x00 }) // flags
                .Concat(body).ToArray();
        }

        var frames = Frame("TIT2", title).Concat(Frame("TPE1", artist)).ToArray();
        // Tag size (syncsafe) excludes the 10-byte header.
        var tagSize = Syncsafe(frames.Length);
        var header = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00 }
            .Concat(tagSize).ToArray();
        return header.Concat(frames).Concat(MelodyBridge.Tests.TestAudio.MinimalMp3()).ToArray();
    }

    /// <summary>Docker-style 7-bit syncsafe integer encoding.</summary>
    private static byte[] Syncsafe(int value) => new[]
    {
        (byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F),
        (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F),
    };

    /// <summary>Runs docker with arguments; throws with output on failure.</summary>
    public static string Docker(string arguments, int timeoutSeconds = 60)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutSeconds * 1000)) throw new TimeoutException($"docker {arguments}");
        if (p.ExitCode != 0) throw new InvalidOperationException(
            $"docker {arguments} failed ({p.ExitCode}): {stderr.Trim()}");
        return stdout;
    }

    /// <summary>True when the docker daemon answers and the image is present.</summary>
    public static bool DockerImageReady(string image)
    {
        try { return Docker($"image inspect {image}").Length > 0; }
        catch { return false; }
    }
}
