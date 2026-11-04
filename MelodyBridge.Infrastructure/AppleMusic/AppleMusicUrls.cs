using System.Text.RegularExpressions;

namespace MelodyBridge.Infrastructure.AppleMusic;

/// <summary>
/// One parsed Apple Music playlist reference. Public playlists need only the
/// catalog id; library playlists carry the marker MelodyBridge uses to route
/// them through the connected account.
/// </summary>
public sealed record AppleMusicPlaylistRef(
    string Id,
    string Storefront,
    bool IsLibrary = false,
    bool IsLibrarySongs = false,
    bool StorefrontFromUrl = false)
{
    /// <summary>True when this reference cannot be fetched without an account.</summary>
    public bool NeedsAccount => IsLibrary || IsLibrarySongs;

    /// <summary>The URL or marker stored as the playlist source.</summary>
    public string SourceUrl => IsLibrarySongs
        ? AppleMusicUrls.LikedMarker
        : IsLibrary
            ? AppleMusicUrls.LibrarySourceUrl(Id)
            : AppleMusicUrls.CanonicalUrl(Storefront, Id);
}

/// <summary>
/// Parses Apple Music playlist input. Public playlist links are accepted in
/// every shape Apple hands out, including the older itunes.apple.com host and
/// links without a storefront. Album and song links are deliberately refused
/// so a pasted song never gets treated as a one-track playlist.
/// </summary>
public static partial class AppleMusicUrls
{
    public const string DefaultStorefront = "us";
    public const string LikedMarker = "applemusic:library-songs";
    public const string LibraryPrefix = "applemusic:library:";

    [GeneratedRegex(@"^pl\.[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BarePlaylistIdRegex();

    [GeneratedRegex(@"^p\.[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BareLibraryIdRegex();

    [GeneratedRegex(
        @"^https?://(?:www\.)?(?:music|itunes)\.apple\.com/(?:(?<sf>[a-z]{2})/)?playlist/(?:[^/?#]+/)?(?<id>pl\.[A-Za-z0-9._-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistUrlRegex();

    [GeneratedRegex(
        @"^https?://(?:www\.)?(?:music|itunes)\.apple\.com/(?:(?<sf>[a-z]{2})/)?(?:album|song|artist)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherMediaUrlRegex();

    /// <summary>
    /// Returns true when the value names an Apple Music playlist or one of
    /// the account-only markers. Album, song and artist links return false.
    /// </summary>
    public static bool CanHandle(string? input)
        => TryParse(input, out _);

    /// <summary>
    /// Parses a playlist URL, raw playlist id or account marker. Throws an
    /// ArgumentException with a useful message when the input is not one.
    /// </summary>
    public static AppleMusicPlaylistRef Parse(string? input)
    {
        if (TryParse(input, out var reference)) return reference;
        throw new ArgumentException(
            "Enter an Apple Music playlist link or playlist id (pl.xxx or pl.u-xxx). "
            + "Album, song and artist links are not playlists.",
            nameof(input));
    }

    /// <summary>Tries to parse a playlist URL, raw id or account marker.</summary>
    public static bool TryParse(string? input, out AppleMusicPlaylistRef reference)
    {
        reference = null!;
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0) return false;

        if (value.Equals(LikedMarker, StringComparison.OrdinalIgnoreCase))
        {
            reference = new AppleMusicPlaylistRef("library-songs", DefaultStorefront, IsLibrarySongs: true);
            return true;
        }

        if (value.StartsWith(LibraryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var libraryId = value[LibraryPrefix.Length..].Trim();
            if (!BareLibraryIdRegex().IsMatch(libraryId)) return false;
            reference = new AppleMusicPlaylistRef(libraryId, DefaultStorefront, IsLibrary: true);
            return true;
        }

        // A raw playlist id is the simplest paste.
        if (BarePlaylistIdRegex().IsMatch(value))
        {
            reference = new AppleMusicPlaylistRef(value, DefaultStorefront);
            return true;
        }

        // An album, song or artist URL never becomes a playlist.
        if (OtherMediaUrlRegex().IsMatch(value)) return false;

        var match = PlaylistUrlRegex().Match(value);
        if (!match.Success) return false;

        var storefront = match.Groups["sf"].Success
            ? match.Groups["sf"].Value.ToLowerInvariant()
            : DefaultStorefront;
        reference = new AppleMusicPlaylistRef(
            match.Groups["id"].Value, storefront, StorefrontFromUrl: match.Groups["sf"].Success);
        return true;
    }

    /// <summary>Extracts the playlist id, or null when the input is not a playlist.</summary>
    public static string? ExtractPlaylistId(string? input)
        => TryParse(input, out var reference) ? reference.Id : null;

    /// <summary>The canonical public URL for a catalog or shared playlist.</summary>
    public static string CanonicalUrl(string storefront, string playlistId)
        => $"https://music.apple.com/{(string.IsNullOrWhiteSpace(storefront) ? DefaultStorefront : storefront)}/playlist/{playlistId}";

    /// <summary>The source marker for a library-only playlist.</summary>
    public static string LibrarySourceUrl(string libraryId)
        => LibraryPrefix + libraryId;

    /// <summary>
    /// The source URL a batch import should store for one library playlist.
    /// A playlist that Apple also exposes in the catalog uses the public id,
    /// so importing it by link and by account updates one row instead of two.
    /// </summary>
    public static string SourceUrlFor(string playlistId, string? storefront = null)
        => playlistId.StartsWith("p.", StringComparison.Ordinal)
            ? LibrarySourceUrl(playlistId)
            : CanonicalUrl(storefront ?? DefaultStorefront, playlistId);
}
