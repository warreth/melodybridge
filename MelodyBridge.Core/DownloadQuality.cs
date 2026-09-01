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
/// The quality a playlist asks the download waterfall for: a bitrate
/// band between MinKbps and MaxKbps.
///
/// Both bounds are hard: the waterfall skips search hits outside the
/// band and the post-download check rejects files that measure outside
/// it (below-min as well as above-cap). Flac/other lossless formats
/// ignore the band (bitrate bounds make no sense there). Unknown
/// bitrates pass the pre-check and leave the verdict to the measurement.
/// </summary>
public record DownloadQuality(
    AudioFormat Format = AudioFormat.Auto,
    int? MinKbps = null,
    int? MaxKbps = null)
{
    /// <summary>Everything allowed: any format, no band.</summary>
    public static DownloadQuality Any { get; } = new(AudioFormat.Auto, null, null);

    /// <summary>True when the given measured bitrate satisfies the band.</summary>
    public bool IsWithinBand(int? kbps)
        => kbps is null || kbps <= 0
            ? true // unknown: post-download measurement decides
            : (MinKbps is null || kbps >= MinKbps)
              && (MaxKbps is null || kbps <= MaxKbps);

    /// <summary>The yt-dlp --audio-quality argument for this cap (VBR target).</summary>
    public string YtDlpAudioQuality => MaxKbps is > 0 ? $"{MaxKbps}K" : "0";

    /// <summary>Whether the format forces a transcode (Auto means "keep the source codec").</summary>
    public bool NeedsTranscode => Format != AudioFormat.Auto;
}
