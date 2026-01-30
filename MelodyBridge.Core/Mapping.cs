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
        // Simple error checking for null input
        if (quality == null)
            return [];
        var results = new List<(Platform, DownloadSource)>();


        if (quality.Format == MediaType.MP3 && quality.Bitrate == 128)
        {
            results.Add((Platform.Zero, DownloadSource.streamrip)); //Quality "0"
        }

        if (quality.Format == MediaType.OPUS && quality.Bitrate == 320)
        {
            results.Add((Platform.AmazonMusic, DownloadSource.squidwtf));
        }

        if (quality.Format == MediaType.FLAC && quality.Bitrate == 320)
        {
            results.Add((Platform.One, DownloadSource.streamrip)); //Quality "1"
        }

        if (quality.Format == MediaType.MP3 && quality.Bitrate == 320)
        {
            results.Add((Platform.Soundcloud, DownloadSource.squidwtf));
            results.Add((Platform.Qobuz, DownloadSource.squidwtf));
            results.Add((Platform.Unknown, DownloadSource.jumodl));
        }

        if (quality.Format == MediaType.AAC && quality.Bitrate == 320)
        {
            results.Add((Platform.Tidal, DownloadSource.squidwtf));
        }

        if (quality.Format == MediaType.OPUS && quality.Bitrate == 320)
        {
            results.Add((Platform.AmazonMusic, DownloadSource.squidwtf));
        }

        if (quality.Format == MediaType.FLAC && quality.Bitrate == 24)
        {
            results.Add((Platform.Qobuz, DownloadSource.squidwtf));
            results.Add((Platform.AmazonMusic, DownloadSource.squidwtf));
            results.Add((Platform.Tidal, DownloadSource.squidwtf));
            results.Add((Platform.Unknown, DownloadSource.dabmusicxyz));
            results.Add((Platform.Unknown, DownloadSource.jumodl));
            results.Add((Platform.Three, DownloadSource.streamrip)); //Quality "3"
        }

        if (quality.Format == MediaType.FLAC && quality.Bitrate == 16)
        {
            results.Add((Platform.Unknown, DownloadSource.jumodl));
            results.Add((Platform.Two, DownloadSource.streamrip)); //Quality "2"
        }

        if (quality.Format == MediaType.FLAC && quality.Bitrate == 192)
        {
            results.Add((Platform.Tidal, DownloadSource.squidwtf));
        }

        else
        {
            // No matches found (user provided unsupported quality)
            return [];
        }
        // Return the list of results (may be empty)
        return results;
    }
}