using System.Text.RegularExpressions;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Helpers;

/// <summary>
/// Shared helper methods used across multiple music providers.
/// Consolidates URL parsing, platform detection, and service-name mapping
/// to eliminate duplication between SquidWtfProvider, LucidaProvider,
/// DoubleDoubleProvider, and MonochromeProvider.
/// </summary>
public static partial class ProviderHelpers
{
    /// <summary>
    /// Detects the music platform from a URL by checking for known domain names.
    /// </summary>
    public static Platform DetectPlatform(string url)
    {
        if (url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase)) return Platform.Qobuz;
        if (url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase)) return Platform.Tidal;
        if (url.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)) return Platform.Deezer;
        if (url.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return Platform.AmazonMusic;
        if (url.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase)) return Platform.Soundcloud;
        if (url.Contains("soundcloud", StringComparison.OrdinalIgnoreCase)) return Platform.Soundcloud;
        if (url.Contains("spotify.com", StringComparison.OrdinalIgnoreCase)) return Platform.Spotify;
        return Platform.Unknown;
    }

    /// <summary>
    /// Extracts a Qobuz track ID from a URL.
    /// Patterns: https://www.qobuz.com/track/123456 or ?track_id=123456
    /// </summary>
    public static bool TryExtractQobuzTrackId(string url, out long id)
    {
        id = 0;
        var match = Regex.Match(url, @"(?:track/|track_id=)(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var parsed))
        {
            id = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts a TIDAL track ID from a URL.
    /// Patterns: https://tidal.com/browse/track/123456 or track=123456
    /// </summary>
    public static bool TryExtractTidalTrackId(string url, out long id)
    {
        id = 0;
        var match = Regex.Match(url, @"track[/=](\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var parsed))
        {
            id = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Maps a <see cref="Platform"/> enum value to the service-name string
    /// used by download provider APIs (lucida.to, doubledouble.top, etc.).
    /// </summary>
    public static string MapPlatformToService(Platform platform) => platform switch
    {
        Platform.Tidal => "tidal",
        Platform.Qobuz => "qobuz",
        Platform.Deezer => "deezer",
        Platform.Soundcloud => "soundcloud",
        Platform.AmazonMusic => "amazon",
        Platform.Spotify => "spotify",
        _ => "tidal",
    };
}
