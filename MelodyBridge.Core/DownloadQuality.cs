namespace MelodyBridge.Core;

/// <summary>Container file formats a downloader can be asked to produce.</summary>
public enum AudioFormat
{
    /// <summary>Whatever the plugin's best source gives (no re-encode).</summary>
    Auto,
    Mp3,
    Flac,
    Opus,
    Aac,
}

/// <summary>
/// The quality a playlist asks the download waterfall for. Plugins that
/// cannot produce the exact format fall back to their best and say so
/// in the result; the bitrate range is a hard gate (files outside it are
/// rejected or at least flagged).
/// </summary>
public record DownloadQuality(
    AudioFormat Format = AudioFormat.Auto,
    int MinKbps = 128,
    int? MaxKbps = null)
{
    /// <summary>Everything allowed: any format, no ceiling.</summary>
    public static DownloadQuality Any { get; } = new(AudioFormat.Auto, 0, null);

    public bool IsBitrateInRange(int? kbps)
        => kbps is null || kbps <= 0
            ? true // unknown: cannot gate, let the post-download check decide
            : kbps >= MinKbps && (MaxKbps is null || kbps <= MaxKbps);
}
