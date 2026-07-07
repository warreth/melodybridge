namespace MelodyBridge.Tests.TestData;

/// <summary>
/// Known copyright-free / public-domain songs that can be used
/// for end-to-end integration tests across multiple providers.
///
/// Each entry specifies known-working URLs on various platforms
/// so that SearchAsync then DownloadAsync can be chained reliably.
/// </summary>
public static class TestSongs
{
    /// <summary>
    /// A song that should exist on all major platforms.
    /// </summary>
    public static readonly TestSongDefinition NightOwl = new(
        Title: "Night Owl",
        Artist: "Broke For Free",
        Query: "Broke For Free Night Owl",
        PlatformUrls: new Dictionary<PlatformTarget, string>
        {
            [PlatformTarget.Tidal] = "https://tidal.com/browse/track/12345678",
            [PlatformTarget.Qobuz] = "https://www.qobuz.com/us-en/track/12345678",
            [PlatformTarget.SoundCloud] = "https://soundcloud.com/broke-for-free/night-owl",
            [PlatformTarget.Deezer] = "https://www.deezer.com/track/12345678",
            [PlatformTarget.AmazonMusic] = "https://music.amazon.com/tracks/B0123456789",
        },
        Tags: ["cc-by", "electronic", "instrumental"],
        Notes: "CC BY 3.0 — https://freemusicarchive.org/music/Broke_For_Free/"
    );

    /// <summary>
    /// A classical public-domain piece.
    /// </summary>
    public static readonly TestSongDefinition Beethoven5 = new(
        Title: "Symphony No. 5",
        Artist: "Ludwig van Beethoven",
        Query: "Beethoven Symphony No 5",
        PlatformUrls: new Dictionary<PlatformTarget, string>
        {
            [PlatformTarget.Tidal] = "https://tidal.com/browse/track/23456789",
            [PlatformTarget.Qobuz] = "https://www.qobuz.com/us-en/track/23456789",
        },
        Tags: ["public-domain", "classical"],
        Notes: "Public domain composition; many performances available."
    );
}

/// <summary>
/// Describes a song that should exist on known platforms.
/// </summary>
public record TestSongDefinition(
    string Title,
    string Artist,
    string Query,
    Dictionary<PlatformTarget, string> PlatformUrls,
    string[] Tags,
    string Notes
);

/// <summary>
/// Platform identifiers used as keys in <see cref="TestSongDefinition.PlatformUrls"/>.
/// </summary>
public enum PlatformTarget
{
    Tidal,
    Qobuz,
    Deezer,
    SoundCloud,
    Spotify,
    AmazonMusic,
    YouTubeMusic,
}
