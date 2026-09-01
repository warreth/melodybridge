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
/// The quality a playlist asks the download waterfall for.
///
/// MaxKbps is a hard ceiling: plugins must not produce files above it
/// and the post-download check rejects any that slip through. Flac/other
/// lossless formats ignore it (a cap makes no sense there).
/// </summary>
public record DownloadQuality(
    AudioFormat Format = AudioFormat.Auto,
    int? MaxKbps = null)
{
    /// <summary>Everything allowed: any format, no ceiling.</summary>
    public static DownloadQuality Any { get; } = new(AudioFormat.Auto, null);

    /// <summary>True when the given measured bitrate satisfies the cap.</summary>
    public bool IsWithinCap(int? kbps)
        => MaxKbps is null || kbps is null || kbps <= 0
            ? true // no cap, or unknown: post-download measurement decides
            : kbps <= MaxKbps;

    /// <summary>The yt-dlp --audio-quality argument for this cap (VBR target).</summary>
    public string YtDlpAudioQuality => MaxKbps is > 0 ? $"{MaxKbps}K" : "0";

    /// <summary>Whether the format forces a transcode (Auto means "keep the source codec").</summary>
    public bool NeedsTranscode => Format != AudioFormat.Auto;
}
