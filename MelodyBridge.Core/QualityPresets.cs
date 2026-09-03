namespace MelodyBridge.Core;

/// <summary>
/// The named quality presets every quality picker offers. A preset is one
/// bitrate rule (and, for Lossless, a fallback rule) expressed with the
/// same DownloadQuality the waterfall already understands, so plugins and
/// gates need no new concepts. The stored string is "preset:&lt;id&gt;";
/// the raw "mp3:192-320" strings remain the advanced path.
/// </summary>
public static class QualityPresets
{
    public enum Id
    {
        /// <summary>Small files: any format up to 160 kbps.</summary>
        SpaceSaver,
        /// <summary>Good files: any lossy format up to 320 kbps; lossless is rejected to save space.</summary>
        HighQuality,
        /// <summary>FLAC/ALAC/WAV when a source has it, otherwise the best lossy up to 320 kbps.</summary>
        Lossless,
        /// <summary>Whatever the sources give, no filter at all.</summary>
        Auto,
    }

    public static readonly (Id Id, string Stored, string Label, string Blurb)[] All =
    {
        (Id.SpaceSaver, "preset:saver", "Space Saver", "Small files, sounds fine"),
        (Id.HighQuality, "preset:high", "High Quality", "Full quality, no huge files"),
        (Id.Lossless, "preset:lossless", "Lossless", "Perfect copies when possible"),
        (Id.Auto, "auto", "No filter", "Takes whatever a source gives"),
    };

    /// <summary>The short description shown next to each option.</summary>
    public static string? BlurbFor(string stored)
        => All.FirstOrDefault(p => p.Stored == stored).Blurb;

    /// <summary>True when the stored string names a preset ("preset:*" or the plain "auto").</summary>
    public static bool IsPreset(string? stored)
        => TryParse(stored, out _);

    public static bool TryParse(string? stored, out Id preset)
    {
        preset = Id.Auto;
        stored = stored?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(stored)) return false;
        foreach (var candidate in All)
            if (candidate.Stored == stored)
            {
                preset = candidate.Id;
                return true;
            }
        return false;
    }

    /// <summary>
    /// The download rule a preset maps to. Lossless asks for FLAC first;
    /// the fallback (best lossy up to 320 kbps) is retried when no source
    /// has a lossless copy.
    /// </summary>
    public static (DownloadQuality primary, DownloadQuality? fallback) ToQuality(Id preset) => preset switch
    {
        // Cap at 160: lossless (~800+) and high-bitrate files are both
        // rejected, whatever container they come in.
        Id.SpaceSaver => (new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160), null),
        // Cap at 320: keeps every good lossy rip, rejects FLAC/WAV sizes.
        Id.HighQuality => (new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 320), null),
        // FLAC from whoever has it; otherwise the best lossy file.
        Id.Lossless => (new DownloadQuality(AudioFormat.Flac), new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 320)),
        _ => (DownloadQuality.Any, null),
    };
}
