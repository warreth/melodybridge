using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// The one place that knows which stored server kinds mean which API:
/// "Navidrome", "OpenSubsonic", "Gonic" and friends are all the Subsonic
/// stack. The directory factory, the sync-job wizard/runner and the
/// Settings page resolve kinds through here, so a new alias is one entry,
/// not four edits.
/// </summary>
public static class MediaServerKinds
{
    public const string Jellyfin = "Jellyfin";
    public const string Plex = "Plex";
    public const string Subsonic = "Subsonic";

    /// <summary>Stored kinds that mean another kind's API.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Navidrome"] = Subsonic,
        ["OpenSubsonic"] = Subsonic,
        ["Gonic"] = Subsonic,
        ["Airsonic"] = Subsonic,
        ["Ampache"] = Subsonic,
        ["Funkwhale"] = Subsonic,
    };

    /// <summary>The canonical kind a stored kind means; the trimmed value when it is unknown.</summary>
    public static string Canonical(string? kind)
    {
        var trimmed = kind?.Trim() ?? string.Empty;
        return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    /// <summary>True when a stored kind speaks the Subsonic/OpenSubsonic API.</summary>
    public static bool IsSubsonic(string? kind)
        => string.Equals(Canonical(kind), Subsonic, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a stored profile kind can serve an output target.</summary>
    public static bool MatchesOutputTarget(string? kind, OutputTargetType target) => target switch
    {
        OutputTargetType.JellyfinApi => string.Equals(Canonical(kind), Jellyfin, StringComparison.OrdinalIgnoreCase),
        OutputTargetType.PlexApi => string.Equals(Canonical(kind), Plex, StringComparison.OrdinalIgnoreCase),
        OutputTargetType.SubsonicApi => IsSubsonic(kind),
        _ => false,
    };

    /// <summary>The API target a profile kind publishes to. Unknown kinds
    /// read as Jellyfin, the wizard's historical default.</summary>
    public static OutputTargetType OutputTargetFor(string? kind)
    {
        if (MatchesOutputTarget(kind, OutputTargetType.PlexApi)) return OutputTargetType.PlexApi;
        if (MatchesOutputTarget(kind, OutputTargetType.SubsonicApi)) return OutputTargetType.SubsonicApi;
        return OutputTargetType.JellyfinApi;
    }
}
