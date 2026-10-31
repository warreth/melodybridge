using System.Text.RegularExpressions;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.FlacMusic;

/// <summary>
/// Pure format and URL rules shared by the FLAC Music search provider and
/// the downloader. No HTTP and no state.
/// </summary>
public static class FlacMusicSource
{
    private static readonly Regex ChineseTitleSuffix = new(
        @"\s*[-\u2013\u2014]\s*《.*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The format that fits the requested quality. FLAC wins for lossless
    /// and unrestricted requests; a bitrate cap walks down to the highest
    /// MP3 that fits. AAC and Opus are not offered by the site.
    /// </summary>
    public static FlacMusicClient.Format? SelectFormat(
        IReadOnlyList<FlacMusicClient.Format> formats, DownloadQuality quality)
    {
        var flac = formats
            .Where(f => f.IsFlac)
            .OrderByDescending(f => f.BitrateKbps)
            .FirstOrDefault();
        var mp3 = formats
            .Where(f => !f.IsFlac)
            .OrderByDescending(f => f.BitrateKbps)
            .ToList();

        if (quality.Format == AudioFormat.Flac) return flac;
        if (quality.Format is AudioFormat.Aac or AudioFormat.Opus) return null;
        if (quality.Format == AudioFormat.Mp3)
            return mp3.FirstOrDefault(f => quality.IsWithinBand(f.BitrateKbps));

        // Auto: real lossless when no cap rules it out, otherwise the best
        // lossy file inside the band.
        var losslessAllowed = quality.MaxKbps is null or >= 900
            || quality.MinKbps is >= 900;
        if (losslessAllowed && flac is not null) return flac;
        return mp3.FirstOrDefault(f => quality.IsWithinBand(f.BitrateKbps));
    }

    public static int QualityRank(DownloaderSearchHit hit)
        => hit.BitrateKbps ?? 10000;

    /// <summary>The encoded URL the search hit carries.</summary>
    public static string EncodeSourceUrl(
        FlacMusicClient.Track track, string title, DownloadQuality quality)
    {
        var formats = string.Join(",",
            track.Formats.Select(f => $"{f.Name}:{f.BitrateKbps}"));
        var query = new List<string>
        {
            "formats=" + Uri.EscapeDataString(formats),
            "artist=" + Uri.EscapeDataString(track.Artist ?? ""),
            "title=" + Uri.EscapeDataString(title),
        };
        if (!string.IsNullOrWhiteSpace(track.Album))
            query.Add("album=" + Uri.EscapeDataString(track.Album));
        if (track.Duration is { } duration)
            query.Add("duration=" + ((int)Math.Round(duration.TotalSeconds)).ToString());
        if (!string.IsNullOrWhiteSpace(track.Time))
            query.Add("time=" + Uri.EscapeDataString(track.Time));
        if (!string.IsNullOrWhiteSpace(track.Sign))
            query.Add("sign=" + Uri.EscapeDataString(track.Sign));
        return $"{FlacMusicClient.DefaultBaseUrl}/track/{Uri.EscapeDataString(track.Platform)}/"
            + $"{Uri.EscapeDataString(track.Id)}?{string.Join("&", query)}";
    }

    /// <summary>"Shoot to Thrill-《Iron Man 2》" -> "Shoot to Thrill".</summary>
    public static string CleanTitle(string title)
    {
        var cleaned = ChineseTitleSuffix.Replace(title, "").Trim();
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }
}
