using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Server.Components.Shared;

/// <summary>
/// One place that turns a track row into the quality, size and duration
/// strings the UI shows. Used by the playlist details page and the
/// library page so both display the same facts the same way.
/// </summary>
public static class TrackDisplay
{
    /// <summary>Bitrate + container, e.g. "320 kbps · flac", empty when nothing is known.</summary>
    public static string Quality(TrackEntity t)
        => Quality(t.Bitrate, t.SampleRateHz, t.MediaType);

    /// <summary>Scalar variant for projections that do not carry the whole entity.</summary>
    public static string Quality(int? bitrate, int? sampleRateHz, string? mediaType)
    {
        var parts = new List<string>();
        if (bitrate is > 0) parts.Add($"{bitrate} kbps");
        if (sampleRateHz is > 0) parts.Add($"{sampleRateHz / 1000.0:0.#} kHz");
        if (!string.IsNullOrWhiteSpace(mediaType)) parts.Add(mediaType);
        return string.Join(" · ", parts);
    }

    /// <summary>Human file size ("3.4 MB"), empty when unknown.</summary>
    public static string Size(TrackEntity t) => Size(t.FileSizeBytes);

    /// <summary>Scalar variant for projections that do not carry the whole entity.</summary>
    public static string Size(long? bytes)
    {
        if (bytes is not > 0) return string.Empty;
        return bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
            : $"{bytes / 1024.0:0.#} KB";
    }

    public static string Duration(long? ms)
        => ms is > 0
            ? TimeSpan.FromMilliseconds(ms.Value).ToString(@"mm\:ss")
            : "-";
}
