using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;
using Playlist = MelodyBridge.Core.Playlist;
using Track = MelodyBridge.Core.Track;
using UserPlaylist = MelodyBridge.Core.UserPlaylist;

namespace MelodyBridge.Infrastructure.Accounts;

/// <summary>
/// The unofficial Spotify account connection: the user's own web session
/// cookie drives Spotify's internal web-player API, so a free account can
/// import its liked songs and playlists without a developer app.
///
/// This exists because Spotify only lets a Premium owner register the app
/// that the official connection needs, and because the file routes
/// (Exportify, the data export) are manual. It is a trade the user makes
/// explicitly:
///
/// - the sp_dc cookie travels to Spotify only, but it is a live session
///   credential of their account: anyone holding it can act as them
/// - Spotify can change the internal API without notice: this path breaks
///   when it does, and the maintainers of the secret list have to catch up
/// - what an account that trips Spotify's radar is theirs to decide; a
///   burner account removes most of that risk
///
/// The API itself is the one the web player uses, with pinned query hashes.
/// This is a C# port of the approach in the GPL-3.0 Jellyfin Spotify import
/// plugin by Viperinius
/// (https://github.com/Viperinius/jellyfin-plugin-spotify-import), modified
/// for MelodyBridge in October 2026, not a fork of it. MelodyBridge never
/// asks for a password and never refreshes tokens on Spotify's behalf: the
/// session cookie is the whole credential.
/// </summary>
public class SpotifyUnofficialAccountProvider : IAccountSourceProvider
{
    public const string ProviderName = "Spotify (unofficial)";

    /// <summary>Stable external id of the liked-songs snapshot.</summary>
    public const string LikedId = "spotify-web-liked";

    /// <summary>The SourceUrl the liked-songs snapshot is stored under.</summary>
    public const string LikedSourceUrl = "spotify:web-liked";

    private const string EnabledKey = "enabled";
    private const string CookieKey = "cookie";
    private const string SecretsUrlKey = "secrets_url";

    // Spotify answers 50 items per page happily; larger pages are clamped.
    private const int PageSize = 50;

    // A runaway guard: a library bigger than this is not a personal library.
    private const int MaxItems = 20_000;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0";

    private readonly AccountTokenStore _settings;
    private readonly SpotifyWebSession _session;
    private readonly HttpClient _http;
    private readonly ILogger<SpotifyUnofficialAccountProvider> _logger;

    public string Name => ProviderName;

    /// <summary>
    /// Pause between library pages. The web player spaces its own requests
    /// and a burst is the quickest way to get this unofficial route rate
    /// limited; tests set it to zero.
    /// </summary>
    internal TimeSpan PageDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public SpotifyUnofficialAccountProvider(
        AccountTokenStore settings,
        ILogger<SpotifyUnofficialAccountProvider> logger)
        : this(settings,
            new SpotifyWebSession(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SpotifyWebSession>.Instance),
            null,
            logger)
    {
    }

    /// <summary>Test seam: a fake HTTP handler instead of a real client.</summary>
    internal SpotifyUnofficialAccountProvider(
        AccountTokenStore settings,
        SpotifyWebSession session,
        HttpClient? http,
        ILogger<SpotifyUnofficialAccountProvider> logger)
    {
        _settings = settings;
        _session = session;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logger = logger;
    }

    /// <summary>True when the user enabled the connection and stored a cookie.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (await _settings.GetSettingAsync(ProviderName, EnabledKey, ct) != "true")
            return false;
        var cookie = await _settings.GetSettingAsync(ProviderName, CookieKey, ct);
        return !string.IsNullOrWhiteSpace(cookie);
    }

    /// <summary>
    /// The unofficial connection has no OAuth flow: the user pastes the
    /// sp_dc cookie. Kept so the interface stays whole.
    /// </summary>
    public Task<string> BeginLoginAsync(string redirectUrl, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "The unofficial Spotify connection uses your browser session cookie, not an OAuth app. "
            + "Paste the sp_dc value in the account settings.");

    /// <inheritdoc cref="BeginLoginAsync"/>
    public Task<string> CompleteLoginAsync(string redirectQuery, string redirectUrl,
        CancellationToken ct = default)
        => throw new InvalidOperationException(
            "The unofficial Spotify connection has no login callback. Paste the sp_dc cookie in the "
            + "account settings instead.");

    /// <summary>Disables the connection and drops the stored cookie.</summary>
    public Task LogoutAsync(CancellationToken ct = default)
        => _settings.ClearAsync(ProviderName, ct);

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => _settings.GetSettingAsync(ProviderName, key, ct);

    public Task SaveSettingAsync(string key, string value, CancellationToken ct = default)
        => _settings.SaveSettingAsync(ProviderName, key, value, ct);

    /// <summary>
    /// Enables or disables the connection. Enabling stores the cookie and
    /// throws away any cached token; disabling keeps the cookie so the
    /// switch is not destructive.
    /// </summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        _session.ForgetToken();
        await _settings.SaveSettingAsync(ProviderName, EnabledKey, enabled ? "true" : "false", ct);
    }

    /// <summary>
    /// A short human status for the settings page: how many liked songs and
    /// playlists the session can see.
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var playlists = await GetUserPlaylistsAsync(ct);
        var liked = await GetLikedPlaylistAsync(ct);
        var version = _session.LastSecretVersion is { } v ? $" (secrets v{v})" : string.Empty;
        return $"Session works: {liked.Tracks?.Count ?? 0} liked songs and "
               + $"{playlists.Count} playlists{version}.";
    }

    public async Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        var result = new List<UserPlaylist>();
        var offset = 0;
        var scanned = 0;
        var total = 0;
        while (offset < MaxItems)
        {
            var data = await QueryAsync(SpotifyWebGraphQl.LibraryPlaylistsBody(PageSize, offset), ct);
            if (!SpotifyWebGraphQl.TryGetPath(data, out var container, "me", "libraryV3"))
                throw new InvalidOperationException(
                    "Spotify's library answer had an unknown shape; the unofficial connection needs an update.");

            total = SpotifyWebGraphQl.TotalCount(container);
            var page = SpotifyWebGraphQl.Items(container).ToList();
            scanned += page.Count;

            foreach (var item in page)
            {
                // The liked-songs row is not a playlist: it has its own import.
                var uri = item.TryGetProperty("item", out var node) ? SpotifyWebGraphQl.StringField(node, "_uri") : null;
                if (uri == "spotify:collection:tracks") continue;

                if (SpotifyWebGraphQl.MapLibraryPlaylist(item) is { } playlist)
                    result.Add(playlist);
            }

            // The cursor follows the page the API returned, and the count is
            // the items scanned, not the playlists kept: the liked-songs row
            // must not turn a full page into an extra empty request.
            if (page.Count == 0 || total > 0 && scanned >= total
                || total <= 0 && page.Count < PageSize)
                break;
            offset += page.Count;
            await DelayBetweenPagesAsync(ct);
        }

        if (total > 0 && scanned < total)
            _logger.LogWarning(
                "Spotify web session listed {Count} playlists but read only {Scanned} of {Total} library items",
                result.Count, scanned, total);

        _logger.LogInformation("Spotify web session listed {Count} playlists", result.Count);
        return result;
    }

    public async Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
    {
        var tracks = new List<Track>();
        var offset = 0;
        var scanned = 0;
        var total = 0;
        while (offset < MaxItems)
        {
            var data = await QueryAsync(SpotifyWebGraphQl.LikedTracksBody(PageSize, offset), ct);
            if (!SpotifyWebGraphQl.TryGetPath(data, out var container, "me", "library", "tracks"))
                throw new InvalidOperationException(
                    "Spotify's liked-songs answer had an unknown shape; the unofficial connection needs an update.");

            total = SpotifyWebGraphQl.TotalCount(container);
            var page = SpotifyWebGraphQl.Items(container).ToList();
            scanned += page.Count;

            foreach (var item in page)
            {
                if (!SpotifyWebGraphQl.TryGetTrackNode(item, out var wrapperUri, out var trackData))
                    continue;
                if (SpotifyWebGraphQl.MapTrack(trackData, wrapperUri, isLiked: true) is { } track)
                    tracks.Add(track);
            }

            if (page.Count == 0 || total > 0 && scanned >= total
                || total <= 0 && page.Count < PageSize)
                break;
            offset += page.Count;
            await DelayBetweenPagesAsync(ct);
        }

        _logger.LogInformation("Spotify web session fetched {Count} liked songs", tracks.Count);

        return new Playlist
        {
            Id = LikedId,
            Name = "Liked songs (Spotify web session)",
            Owner = "you",
            Description = "Your Spotify liked songs, imported through the web session",
            SourceUrl = LikedSourceUrl,
            Tracks = tracks,
            TrackCount = tracks.Count,
            Duration = Sum(tracks),
            // A short answer is not a small library: say so, the store keeps
            // the snapshot marked as partial instead of clean.
            Warning = total > 0 && scanned < total
                ? $"partial fetch: read {scanned} of {total} items"
                : null,
        };
    }

    /// <summary>
    /// Fetches one playlist through the session. Returns null when Spotify
    /// does not hand it over (a playlist the account cannot see, a shape
    /// change), so the caller can fall back to the public fetch.
    /// </summary>
    public async Task<Playlist?> TryGetPlaylistViaAccountAsync(
        string playlistId, CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct)) return null;

        try
        {
            var uri = playlistId.StartsWith("spotify:playlist:", StringComparison.Ordinal)
                ? playlistId
                : $"spotify:playlist:{playlistId}";

            var tracks = new List<Track>();
            JsonElement playlistNode = default;
            var offset = 0;
            var scanned = 0;
            var total = 0;
            while (offset < MaxItems)
            {
                var data = await QueryAsync(SpotifyWebGraphQl.PlaylistBody(PageSize, offset, uri), ct);
                if (!SpotifyWebGraphQl.TryGetPath(data, out playlistNode, "playlistV2"))
                    return null;

                if (!playlistNode.TryGetProperty("content", out var content))
                    return null;

                total = SpotifyWebGraphQl.TotalCount(content);
                var page = SpotifyWebGraphQl.Items(content).ToList();
                scanned += page.Count;

                foreach (var item in page)
                {
                    if (!SpotifyWebGraphQl.TryGetTrackNode(item, out var wrapperUri, out var trackData))
                        continue;
                    if (SpotifyWebGraphQl.MapTrack(trackData, wrapperUri, isLiked: false) is { } track)
                        tracks.Add(track);
                }

                if (page.Count == 0 || total > 0 && scanned >= total
                    || total <= 0 && page.Count < PageSize)
                    break;
                offset += page.Count;
                await DelayBetweenPagesAsync(ct);
            }

            var playlist = SpotifyWebGraphQl.MapPlaylist(playlistNode, tracks);
            if (playlist is not null)
            {
                if (string.IsNullOrEmpty(playlist.Id))
                    playlist.Id = playlistId;
                if (total > 0 && scanned < total)
                {
                    playlist.Warning = $"partial fetch: read {scanned} of {total} items";
                    _logger.LogWarning(
                        "Spotify web session read {Scanned} of {Total} items in playlist {PlaylistId}",
                        scanned, total, playlistId);
                }
            }

            return playlist;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "Spotify web session fetch for {PlaylistId} did not work ({Message}); the public path may still work",
                playlistId, ex.Message);
            return null;
        }
    }

    private async Task<JsonElement> QueryAsync(string body, CancellationToken ct)
    {
        var cookie = await _settings.GetSettingAsync(ProviderName, CookieKey, ct);
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException(
                "No Spotify session cookie is stored; paste it in the account settings.");

        var secretsUrl = await _settings.GetSettingAsync(ProviderName, SecretsUrlKey, ct);
        var token = await _session.GetAccessTokenAsync(cookie, secretsUrl, ct);

        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SpotifyWebGraphQl.QueryUrl)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                response = await _http.SendAsync(request, ct);

                // One backoff per query, honoring Retry-After. The web player
                // paces itself; a burst of pages is what trips the limit.
                if ((int)response.StatusCode != 429 || attempt >= 1) break;

                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks, TimeSpan.FromSeconds(30).Ticks));
                response.Dispose();
                response = null;
                _logger.LogInformation(
                    "Spotify rate limited the web session; retrying in {Wait}", wait);
                await Task.Delay(wait, ct);
            }

            if (response is null)
                throw new InvalidOperationException("Spotify did not answer the web request.");

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The token can be revoked mid-session: drop it so the next
                // attempt mints a fresh one from the cookie.
                _session.ForgetToken();
                throw new InvalidOperationException(
                    "Spotify refused the web session (401). The cookie may have been logged out; "
                    + "copy a fresh sp_dc value in the account settings.");
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Spotify's web API answered {(int)response.StatusCode} for the unofficial connection.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;

            // A GraphQL errors answer is not a shape change: surface it as such
            // (a playlist the account cannot see lands here too, and the caller
            // treats it as "no playlist from this path").
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                var message = errors[0].ValueKind == JsonValueKind.Object
                    && errors[0].TryGetProperty("message", out var first)
                        ? first.GetString()
                        : null;
                throw new InvalidOperationException(
                    $"Spotify's web API refused the request: {message ?? "unknown error"}.");
            }

            if (!SpotifyWebGraphQl.TryGetData(root, out var data))
                throw new InvalidOperationException(
                    "Spotify's web API answered without data; the unofficial connection may need an update.");

            return data.Clone();
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// The pause between library pages. Zero in tests, 250 ms in the app,
    /// the same spacing the Jellyfin plugin uses against this API.
    /// </summary>
    private Task DelayBetweenPagesAsync(CancellationToken ct)
        => PageDelay > TimeSpan.Zero ? Task.Delay(PageDelay, ct) : Task.CompletedTask;

    private static TimeSpan? Sum(IReadOnlyList<Track> tracks)
    {
        var total = TimeSpan.Zero;
        var any = false;
        foreach (var track in tracks)
        {
            if (track.Duration is not { } d) continue;
            total += d;
            any = true;
        }
        return any ? total : null;
    }
}
