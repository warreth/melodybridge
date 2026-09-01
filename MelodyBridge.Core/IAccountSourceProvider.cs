namespace MelodyBridge.Core;

/// <summary>
/// An account connection to a music platform. Providers implement this next
/// to ISourceProvider: the public URL fetcher keeps working without any
/// account, and this interface adds private playlists, the user's own
/// playlists and their liked songs.
/// </summary>
public interface IAccountSourceProvider
{
    /// <summary>Matching ISourceProvider.Name, e.g. "Spotify".</summary>
    string Name { get; }

    /// <summary>True when an account is connected and its token is usable.</summary>
    Task<bool> IsConnectedAsync(CancellationToken ct = default);

    /// <summary>Url that starts the platform's login flow (opened by the UI).</summary>
    Task<string> BeginLoginAsync(string redirectUrl, CancellationToken ct = default);

    /// <summary>
    /// Finishes the login with the query string the platform redirected
    /// back with (code + state or an error). Returns a short human status.
    /// Throws InvalidOperationException on a failed exchange.
    /// </summary>
    Task<string> CompleteLoginAsync(string redirectQuery, string redirectUrl,
        CancellationToken ct = default);

    /// <summary>Forgets the stored account and tokens.</summary>
    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>An account setting (client id, redirect url, ...). Not a secret.</summary>
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);

    /// <summary>Saves an account setting.</summary>
    Task SaveSettingAsync(string key, string value, CancellationToken ct = default);

    /// <summary>All playlists of the logged-in user, including private ones.</summary>
    Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// The logged-in user's liked songs. The id is stable so the result can
    /// be added through the normal playlist add flow.
    /// </summary>
    Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default);
}

/// <summary>One playlist of the logged-in user, for the import picker.</summary>
public record UserPlaylist(
    string Id,
    string Name,
    string? Owner,
    int TrackCount,
    bool IsLikedSongs,
    string? CoverImageUrl = null);

/// <summary>
/// Minimal OAuth token pair an account provider needs. Providers refresh
/// themselves; callers never see expiry handling.
/// </summary>
public record AccountTokens(
    string AccessToken,
    string? RefreshToken,
    DateTime ExpiresAtUtc);
