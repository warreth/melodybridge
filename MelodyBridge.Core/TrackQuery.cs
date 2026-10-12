namespace MelodyBridge.Core;

/// <summary>
/// One track lookup through the plugin waterfall: the metadata a plugin
/// may search on. A record so new matching signals (duration, ISRC) do
/// not keep widening every plugin signature.
///
/// <see cref="Isrc"/> is the strongest signal when present: catalogs that
/// index ISRCs (Qobuz-backed plugins) can find the exact recording even
/// when the title search is noisy. Plugins that cannot use it ignore it
/// and fall back to artist/title as before.
/// </summary>
public record TrackQuery(
    string Artist,
    string Title,
    DownloadQuality Quality,
    TimeSpan? ExpectedDuration = null,
    string? Isrc = null)
{
    /// <summary>True when the ISRC is present and usable.</summary>
    public bool HasIsrc => !string.IsNullOrWhiteSpace(Isrc);

    /// <summary>The clean "artist title" text every provider search uses.</summary>
    public string SearchText => SearchTerms.Query(Artist, Title);
}
