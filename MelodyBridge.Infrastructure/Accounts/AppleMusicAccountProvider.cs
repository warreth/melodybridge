using System.Net;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.AppleMusic;
using Microsoft.Extensions.Logging;
using Playlist = MelodyBridge.Core.Playlist;
using Track = MelodyBridge.Core.Track;
using UserPlaylist = MelodyBridge.Core.UserPlaylist;

namespace MelodyBridge.Infrastructure.Accounts;

/// <summary>
/// The unofficial Apple Music account connection: the user's own
/// media-user-token cookie from a logged-in music.apple.com session drives
/// the library API. Public and shared playlist links keep working without
/// this; the cookie adds playlists that were never shared and the saved
/// library songs.
///
/// The cookie is a live session credential. It is stored in the database in
/// plain text, like the Spotify web session, and Apple can invalidate it at
/// any time. No Apple ID password is ever asked for or stored.
/// </summary>
public class AppleMusicAccountProvider : IAccountSourceProvider
{
    public const string ProviderName = "Apple Music";
    public const string EnabledKey = "enabled";
    public const string TokenKey = "media_user_token";

    /// <summary>Stable id for the saved-songs snapshot.</summary>
    public const string LikedId = "library-songs";

    /// <summary>The SourceUrl the saved-songs snapshot is stored under.</summary>
    public const string LikedSourceUrl = AppleMusicUrls.LikedMarker;

    private const int PageSize = 100;
    private const int MaxItems = 20_000;

    private readonly AccountTokenStore _settings;
    private readonly AppleMusicApiClient _api;
    private readonly ILogger<AppleMusicAccountProvider> _logger;
    private string? _storefront;

    public string Name => ProviderName;

    public AppleMusicAccountProvider(
        AccountTokenStore settings,
        AppleMusicApiClient api,
        ILogger<AppleMusicAccountProvider> logger)
    {
        _settings = settings;
        _api = api;
        _logger = logger;
    }

    /// <summary>The source URL a batch import should store for one playlist.</summary>
    public static string SourceUrlFor(string playlistId, string? storefront = null)
        => AppleMusicUrls.SourceUrlFor(playlistId, storefront);

    /// <summary>The source marker for one library-only playlist.</summary>
    public static string LibrarySourceUrl(string libraryId)
        => AppleMusicUrls.LibrarySourceUrl(libraryId);

    public virtual async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (await _settings.GetSettingAsync(ProviderName, EnabledKey, ct) != "true")
            return false;
        var token = await _settings.GetSettingAsync(ProviderName, TokenKey, ct);
        return !string.IsNullOrWhiteSpace(token);
    }

    public virtual Task<string> BeginLoginAsync(
        string redirectUrl, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "The Apple Music web session has no login callback. Paste the "
            + "media-user-token cookie from your logged-in music.apple.com browser "
            + "in the account settings.");

    public virtual Task<string> CompleteLoginAsync(
        string redirectQuery, string redirectUrl, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "The Apple Music web session has no login callback. Paste the "
            + "media-user-token cookie from your logged-in music.apple.com browser "
            + "in the account settings.");

    /// <summary>Disables the connection and drops the stored cookie.</summary>
    public virtual async Task LogoutAsync(CancellationToken ct = default)
    {
        _storefront = null;
        await _settings.ClearAsync(ProviderName, ct);
    }

    public virtual Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => _settings.GetSettingAsync(ProviderName, key, ct);

    public virtual Task SaveSettingAsync(
        string key, string value, CancellationToken ct = default)
    {
        // A new session may belong to another storefront; do not keep the
        // previous account's answer.
        if (key.Equals(TokenKey, StringComparison.OrdinalIgnoreCase))
            _storefront = null;
        return _settings.SaveSettingAsync(ProviderName, key, value, ct);
    }

    /// <summary>Enables or disables imports without destroying the saved cookie.</summary>
    public virtual Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        => _settings.SaveSettingAsync(ProviderName, EnabledKey, enabled ? "true" : "false", ct);

    /// <summary>
    /// A short status for the settings page. Two one-row reads prove the
    /// session and report the library sizes without walking the library.
    /// </summary>
    public virtual async Task<string> TestAsync(CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);
        using var playlists = await _api.GetAsync(
            "/v1/me/library/playlists?limit=1", token, ct);
        using var songs = await _api.GetAsync(
            "/v1/me/library/songs?limit=1", token, ct);

        var playlistCount = AppleMusicMapper.Total(playlists.RootElement)
                            ?? AppleMusicMapper.DataArray(playlists.RootElement).Count;
        var songCount = AppleMusicMapper.Total(songs.RootElement)
                        ?? AppleMusicMapper.DataArray(songs.RootElement).Count;
        return $"Apple Music works: {playlistCount} playlists and {songCount} library songs.";
    }

    public virtual async Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);
        var storefront = await GetStorefrontAsync(token, ct);
        var result = new List<UserPlaylist>();
        var offset = 0;

        while (offset < MaxItems)
        {
            using var document = await _api.GetAsync(
                $"/v1/me/library/playlists?limit={PageSize}&offset={offset}", token, ct);
            var data = AppleMusicMapper.DataArray(document.RootElement);
            if (data.Count == 0) break;

            foreach (var item in data)
            {
                if (AppleMusicMapper.MapUserPlaylist(item) is { } playlist)
                    result.Add(playlist with { Storefront = storefront });
            }

            offset += data.Count;
            var total = AppleMusicMapper.Total(document.RootElement);
            if (total is { } reported && offset >= reported) break;
            if (string.IsNullOrWhiteSpace(AppleMusicMapper.NextLink(document.RootElement))
                && total is null)
                break;
        }

        _logger.LogInformation("Apple Music library listed {Count} playlists", result.Count);
        return result;
    }

    /// <summary>
    /// Fetches a playlist through the connected account. The input may be a
    /// full link, so a storefront in that link is preserved instead of
    /// silently falling back to the account's own storefront. Returns null
    /// when the account is off or Apple does not hand the playlist over, so
    /// the public fetcher can still try.
    /// </summary>
    public virtual async Task<Playlist?> TryGetPlaylistViaAccountAsync(
        string sourceIdentifier, CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct)) return null;

        try
        {
            var token = await RequireTokenAsync(ct);
            var reference = AppleMusicUrls.TryParse(sourceIdentifier, out var parsed)
                ? parsed
                : null;
            var playlistId = reference?.Id ?? sourceIdentifier.Trim();
            var isLibrary = playlistId.StartsWith("p.", StringComparison.Ordinal);
            var storefront = reference is { StorefrontFromUrl: true }
                ? reference.Storefront
                : await GetStorefrontAsync(token, ct);

            var path = isLibrary
                ? $"/v1/me/library/playlists/{playlistId}?include=tracks&limit[tracks]={PageSize}"
                : $"/v1/catalog/{storefront}/playlists/{playlistId}?include=tracks&limit[tracks]=300";
            var canonical = isLibrary
                ? AppleMusicUrls.LibrarySourceUrl(playlistId)
                : AppleMusicUrls.CanonicalUrl(storefront, playlistId);

            using var first = await _api.GetAsync(path, token, ct);
            var playlist = AppleMusicMapper.MapPlaylist(
                first.RootElement, playlistId, canonical);

            // A library playlist that is also in the catalog keeps the
            // catalog id for dedup, but still refreshes through the library.
            if (!isLibrary && playlist.Id.StartsWith("pl.", StringComparison.Ordinal))
                playlist.SourceUrl = AppleMusicUrls.CanonicalUrl(storefront, playlist.Id);
            else if (isLibrary)
                playlist.SourceUrl = AppleMusicUrls.LibrarySourceUrl(playlistId);

            var next = AppleMusicMapper.NextLink(first.RootElement);
            var pages = 0;
            while (!string.IsNullOrWhiteSpace(next)
                   && playlist.Tracks is { Count: < MaxItems }
                   && pages++ < 200)
            {
                using var page = await _api.GetAsync(next, token, ct);
                playlist.Tracks ??= new List<Track>();
                playlist.Tracks.AddRange(AppleMusicMapper.MapTracks(page.RootElement));
                playlist.TrackCount = playlist.Tracks.Count;
                playlist.Duration = SumDurations(playlist.Tracks);
                next = AppleMusicMapper.NextLink(page.RootElement);
            }

            return playlist;
        }
        catch (AppleMusicApiException ex) when (
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Private playlist without access, a deleted playlist or a dead
            // session: the public path may still work.
            _logger.LogInformation(
                "Apple Music account fetch for {PlaylistId} did not work ({Message}); trying the public path",
                sourceIdentifier, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is AppleMusicTokenException or HttpRequestException)
        {
            _logger.LogInformation(
                "Apple Music account fetch for {PlaylistId} failed ({Message}); trying the public path",
                sourceIdentifier, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The user's saved library songs. Apple has no endpoint that lists
    /// favorite songs, so this is every saved song and every track stays
    /// IsLiked=false. The UI calls it Library songs, never liked songs.
    /// </summary>
    public virtual async Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);
        var tracks = new List<Track>();
        var offset = 0;
        var truncated = false;

        while (offset < MaxItems)
        {
            using var document = await _api.GetAsync(
                $"/v1/me/library/songs?limit={PageSize}&offset={offset}", token, ct);
            var data = AppleMusicMapper.DataArray(document.RootElement);
            if (data.Count == 0) break;
            tracks.AddRange(AppleMusicMapper.MapTracks(document.RootElement));

            offset += data.Count;
            var total = AppleMusicMapper.Total(document.RootElement);
            if (total is { } reported && offset >= reported) break;
            if (string.IsNullOrWhiteSpace(AppleMusicMapper.NextLink(document.RootElement))
                && total is null)
                break;
            if (tracks.Count >= MaxItems)
            {
                truncated = true;
                break;
            }
        }

        if (tracks.Count > MaxItems)
        {
            tracks = tracks.Take(MaxItems).ToList();
            truncated = true;
        }

        return new Playlist
        {
            Id = LikedId,
            Name = "Library songs",
            SourceUrl = LikedSourceUrl,
            Tracks = tracks,
            TrackCount = tracks.Count,
            Duration = SumDurations(tracks),
            Warning = truncated ? $"library truncated at {MaxItems} songs" : null,
        };
    }

    private async Task<string> GetStorefrontAsync(string token, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_storefront)) return _storefront;

        try
        {
            using var document = await _api.GetAsync("/v1/me/storefront", token, ct);
            var data = AppleMusicMapper.DataArray(document.RootElement);
            var storefront = data.Count > 0 ? AppleMusicMapper.String(data[0], "id") : null;
            if (!string.IsNullOrWhiteSpace(storefront))
                return _storefront = storefront;
        }
        catch (Exception ex) when (ex is AppleMusicApiException or AppleMusicTokenException
                                   or HttpRequestException or JsonException)
        {
            _logger.LogInformation(
                "Apple Music storefront lookup failed ({Message}); using {Storefront}",
                ex.Message, AppleMusicUrls.DefaultStorefront);
        }

        // Do not cache a fallback: a later call may reach Apple and get the
        // real storefront.
        return AppleMusicUrls.DefaultStorefront;
    }

    private async Task<string> RequireTokenAsync(CancellationToken ct)
    {
        var token = await _settings.GetSettingAsync(ProviderName, TokenKey, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Paste the Apple Music media-user-token cookie in Settings first.");
        return token.Trim();
    }

    private static TimeSpan? SumDurations(IEnumerable<Track> tracks)
    {
        var total = TimeSpan.Zero;
        var any = false;
        foreach (var track in tracks)
        {
            if (track.Duration is not { } duration) continue;
            total += duration;
            any = true;
        }
        return any ? total : null;
    }
}
