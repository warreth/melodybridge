namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// Magic-byte sniffing for downloaded audio files. Rip sites and mirrors
/// occasionally answer with an HTML error page or a JSON error body under
/// a 200 status; without this check such bytes get saved under an audio
/// extension and only a later probe (or a confused media server) notices.
/// The sniff runs on the first bytes of the file, never on the extension.
/// </summary>
public static class AudioSignature
{
    /// <summary>
    /// Container extension the leading bytes identify (".mp3", ".flac",
    /// ".m4a", ".opus", ".ogg", ".wav", ".aiff", ".webm"), or null when
    /// the file does not start with any known audio signature.
    /// </summary>
    public static string? DetectContainer(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[64];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return DetectContainer(head[..read]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the file's leading bytes look like audio.</summary>
    public static bool LooksLikeAudio(string path) => DetectContainer(path) is not null;

    /// <summary>Pure sniff over a byte span, for tests and streams.</summary>
    public static string? DetectContainer(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return null;

        // "ID3" tagged MP3 (the tag header precedes the frames).
        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return ".mp3";

        // MPEG audio frame sync: 11 set bits, then a valid layer/version.
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
        {
            var layer = (head[1] >> 1) & 0x03;
            // 0 is reserved; the other three layers are all MPEG audio.
            if (layer != 0) return ".mp3";
        }

        // FLAC stream marker.
        if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return ".flac";

        // Ogg container (Vorbis/Opus/FLAC-in-Ogg).
        if (head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
            return head.IndexOf("OpusHead"u8) >= 0 ? ".opus" : ".ogg";

        // ISO-BMFF (MP4/M4A): size + "ftyp" at offset 4.
        if (head.Length >= 8 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
            return ".m4a";

        // ADTS AAC: sync word with layer 0.
        if (head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return ".m4a";

        // RIFF/WAVE.
        if (head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E')
            return ".wav";

        // AIFF.
        if (head.Length >= 12 && head[0] == 'F' && head[1] == 'O' && head[2] == 'R' && head[3] == 'M'
            && head[8] == 'A' && head[9] == 'I' && head[10] == 'F' && head[11] == 'F')
            return ".aiff";

        // Matroska/WebM (EBML): YouTube's opus-in-webm fallback.
        if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return ".webm";

        return null;
    }

    /// <summary>
    /// Replaces a file's extension with the sniffed container when they
    /// disagree, so a FLAC served as .mp3 lands as .flac. Returns the
    /// (possibly moved) path, or null when the bytes are not audio.
    /// </summary>
    public static string? EnsureExtension(string path)
    {
        var container = DetectContainer(path);
        if (container is null) return null;

        var current = Path.GetExtension(path);
        if (string.Equals(current, container, StringComparison.OrdinalIgnoreCase))
            return path;

        var target = Path.ChangeExtension(path, container);
        try
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
            return target;
        }
        catch
        {
            // Renaming is cosmetic; the bytes still sniff correctly.
            return path;
        }
    }
}
