namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Maps a raw logger category to the plugin or subsystem that produced it,
/// so the Logs page can filter by source as well as by area. The waterfall
/// itself and the coordinator keep their own name because their messages
/// are not owned by one source.
/// </summary>
public static class LogSources
{
    public const string Other = "Other";

    /// <summary>Filter order: the waterfall first, then the sources.</summary>
    public static readonly string[] All =
    {
        "Waterfall",
        "Qobuz", "Tidal", "Deezer", "yt-dlp", "SoundCloud",
        "Monochrome", "Jumo", "Lucida", "Archive.org", "Soulseek", "DoubleDouble",
        Other,
    };

    public static string FromCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return Other;

        if (category.Contains("Qobuz", StringComparison.OrdinalIgnoreCase)) return "Qobuz";
        if (category.Contains("Tidal", StringComparison.OrdinalIgnoreCase)) return "Tidal";
        if (category.Contains("Deemix", StringComparison.OrdinalIgnoreCase)) return "Deezer";
        if (category.Contains("DeezerClient", StringComparison.OrdinalIgnoreCase)) return "Deezer";
        if (category.Contains("YtDlp", StringComparison.OrdinalIgnoreCase)) return "yt-dlp";
        if (category.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase)) return "SoundCloud";
        if (category.Contains("Monochrome", StringComparison.OrdinalIgnoreCase)) return "Monochrome";
        if (category.Contains("Jumo", StringComparison.OrdinalIgnoreCase)) return "Jumo";
        if (category.Contains("Lucida", StringComparison.OrdinalIgnoreCase)) return "Lucida";
        if (category.Contains("ArchiveOrg", StringComparison.OrdinalIgnoreCase)) return "Archive.org";
        if (category.Contains("Slskd", StringComparison.OrdinalIgnoreCase)) return "Soulseek";
        if (category.Contains("DoubleDouble", StringComparison.OrdinalIgnoreCase)) return "DoubleDouble";
        if (category.Contains("Download", StringComparison.OrdinalIgnoreCase)) return "Waterfall";

        return Other;
    }
}
