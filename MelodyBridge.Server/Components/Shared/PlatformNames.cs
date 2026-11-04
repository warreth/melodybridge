using MelodyBridge.Core;

namespace MelodyBridge.Server.Components.Shared;

/// <summary>
/// The names a person sees for a playlist source. Kept in one place so a
/// card, a job picker and the dashboard cannot disagree about "Apple Music".
/// </summary>
public static class PlatformNames
{
    public static string Display(Platform platform) => platform switch
    {
        Platform.Spotify => "Spotify",
        Platform.YouTubeMusic => "YouTube Music",
        Platform.AppleMusic => "Apple Music",
        Platform.Tidal => "Tidal",
        Platform.AmazonMusic => "Amazon Music",
        Platform.Qobuz => "Qobuz",
        Platform.Soundcloud => "SoundCloud",
        Platform.Deezer => "Deezer",
        _ => "Unknown",
    };
}
