namespace MelodyBridge.Core;

using MelodyBridge.Core;
/// <summary>
/// Maps TrackQuality to the appropriate Platform.
/// </summary>
public static class PlatformQualityMapper
{
    /// <summary>
    /// Returns a list of possible (Platform, DownloadSource) pairs for a given TrackQuality.
    /// </summary>
    /// <param name="quality">The track quality to map.</param>
    /// <returns>List of (Platform, DownloadSource) pairs, or empty list if none found.</returns>
    public static List<(Platform platform, DownloadSource source)> GetPlatformsForQuality(TrackQuality quality)
    {
        if (quality == null)
            return [];
        var results = new List<(Platform, DownloadSource)>();

        if (quality.Format == MediaType.OPUS && quality.Bitrate == 320)
            results.Add((Platform.AmazonMusic, DownloadSource.squidwtf));

        if (quality.Format == MediaType.MP3 && quality.Bitrate == 320)
        {
            results.Add((Platform.Soundcloud, DownloadSource.squidwtf));
            results.Add((Platform.Qobuz, DownloadSource.squidwtf));
        }

        if (quality.Format == MediaType.AAC && quality.Bitrate == 320)
            results.Add((Platform.Tidal, DownloadSource.squidwtf));

        if (quality.Format == MediaType.FLAC && quality.Bitrate == 24)
        {
            results.Add((Platform.Qobuz, DownloadSource.squidwtf));
            results.Add((Platform.AmazonMusic, DownloadSource.squidwtf));
            results.Add((Platform.Tidal, DownloadSource.squidwtf));
        }

        if (quality.Format == MediaType.FLAC && quality.Bitrate == 192)
            results.Add((Platform.Tidal, DownloadSource.squidwtf));

        return results;
    }
}