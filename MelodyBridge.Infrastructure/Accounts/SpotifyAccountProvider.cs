using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Core;
using AccountTokens = MelodyBridge.Core.AccountTokens;
using IAccountSourceProvider = MelodyBridge.Core.IAccountSourceProvider;
using Playlist = MelodyBridge.Core.Playlist;
using SongID = MelodyBridge.Core.SongID;
using Track = MelodyBridge.Core.Track;
using UserPlaylist = MelodyBridge.Core.UserPlaylist;

namespace MelodyBridge.Infrastructure.Accounts;

/// <summary>
/// Spotify account connection using the official OAuth PKCE flow through
/// SpotifyAPI-NET. Read-only scopes only (private playlists, collaborative
/// playlists, liked songs): the app never writes anything to the account,
/// which is what keeps it safe from moderation.
///
/// The public-playlist fetcher in SpotifySourceProvider keeps working
/// without any of this; when an account IS connected, the store prefers
/// this provider so private playlists and liked songs work too.
/// </summary>
public class SpotifyAccountProvider : IAccountSourceProvider
{
    public const string ProviderName = "Spotify";
    private const string LikedId = "spotify-liked";

    // Scopes: read private + collaborative playlists and the liked library.
    // Read-only: no playlist-modify, no user-modify.
    private static readonly string[] Scopes =
    {
        SpotifyAPI.Web.Scopes.PlaylistReadPrivate,
        SpotifyAPI.Web.Scopes.PlaylistReadCollaborative,
        SpotifyAPI.Web.Scopes.UserLibraryRead,
    };

    private readonly AccountTokenStore _tokens;
    private readonly ILogger<SpotifyAccountProvider> _logger;

    public string Name => ProviderName;

    public SpotifyAccountProvider(
        AccountTokenStore tokens,
        ILogger<SpotifyAccountProvider> logger)
    {
        _tokens = tokens;
        _logger = logger;
    }

    public virtual async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        var tokens = await _tokens.GetTokensAsync(ProviderName, ct);
        return tokens is { AccessToken.Length: > 0 };
    }

    public virtual async Task<string> BeginLoginAsync(string redirectUrl, CancellationToken ct = default)
    {
        // The user's own app (or MelodyBridge's default), set in Settings.
        var clientId = await ReadClientIdAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "No Spotify client id configured. Create an app at developer.spotify.com and paste its Client ID in the account settings.");

        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        var state = Guid.NewGuid().ToString("N");
        // Kept in the database: the app may restart while the user is on
        // the Spotify consent page, and the verifier must survive that.
        await _tokens.SavePendingLoginAsync(ProviderName,
            new AccountTokenStore.PendingLogin(verifier, state, DateTime.UtcNow), ct);

        var login = new LoginRequest(
            new Uri(redirectUrl),
            clientId,
            LoginRequest.ResponseType.Code)
        {
            CodeChallengeMethod = "S256",
            CodeChallenge = challenge,
            // Without State, Spotify never echoes one back and the
            // callback cannot verify the answer belongs to this login.
            State = state,
            Scope = Scopes,
        };
        return login.ToUri().ToString();
    }

    public virtual async Task<string> CompleteLoginAsync(
        string redirectQuery, string redirectUrl, CancellationToken ct = default)
    {
        var query = System.Web.HttpUtility.ParseQueryString(
            redirectQuery.StartsWith('?') ? redirectQuery[1..] : redirectQuery);

        var error = query["error"];
        if (!string.IsNullOrWhiteSpace(error))
        {
            await _tokens.ClearPendingLoginAsync(ProviderName, ct);
            throw new InvalidOperationException($"Spotify login failed: {error}");
        }

        var code = query["code"];
        var state = query["state"];
        var pending = await _tokens.GetPendingLoginAsync(ProviderName, ct);

        // Distinct messages for distinct causes: "something went wrong,
        // try again" hides which half of the handshake failed.
        if (string.IsNullOrWhiteSpace(code) && pending is null)
        {
            _logger.LogWarning(
                "Spotify callback without a code and without a pending login; query was {Query}", redirectQuery);
            throw new InvalidOperationException(
                "Spotify sent no login code and no login was in progress. Start the login again from the accounts settings.");
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("Spotify callback without a code; query was {Query}", redirectQuery);
            throw new InvalidOperationException(
                "Spotify sent no login code back. Start the login again from the accounts settings.");
        }
        if (pending is null)
        {
            _logger.LogWarning(
                "Spotify callback arrived with no pending login left (expired, completed or the app restarted before this login was saved)");
            throw new InvalidOperationException(
                "No Spotify login was in progress, or it expired (an hour at most). Start the login again from the accounts settings.");
        }
        if (state != pending.State)
        {
            // The pending login stays: a stray or forged callback must not
            // be able to cancel the real one. Only the holder of the
            // correct state can finish it.
            _logger.LogWarning("Spotify login state mismatch: got {Got}, expected {Expected}", state, pending.State);
            throw new InvalidOperationException(
                "This Spotify answer does not belong to the login that was started. Start the login again from the accounts settings.");
        }

        var clientId = await ReadClientIdAsync(ct)
                       ?? throw new InvalidOperationException(
                           "Spotify Client ID is missing. Paste it in the account settings.");
        try
        {
            var response = await new OAuthClient().RequestToken(
                new PKCETokenRequest(clientId, code, new Uri(redirectUrl), pending.Verifier));

            await _tokens.SaveTokensAsync(ProviderName, ToTokens(response), ct);
            await _tokens.ClearPendingLoginAsync(ProviderName, ct);
        }
        catch (Exception ex)
        {
            // A failed exchange must not leave the pending login stuck:
            // the next attempt starts a fresh PKCE pair.
            await _tokens.ClearPendingLoginAsync(ProviderName, ct);
            throw new InvalidOperationException(
                $"Spotify token exchange failed: {ex.Message}. Try connecting again.");
        }

        _logger.LogInformation("Spotify account connected");
        return "Spotify account connected";
    }

    public virtual Task LogoutAsync(CancellationToken ct = default)
        => _tokens.ClearAsync(ProviderName, ct);

    public virtual Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => _tokens.GetSettingAsync(ProviderName, key, ct);

    public virtual Task SaveSettingAsync(string key, string value, CancellationToken ct = default)
        => _tokens.SaveSettingAsync(ProviderName, key, value, ct);

    /// <summary>
    /// A client with a fresh token; refreshes once with the refresh token
    /// when the stored one has run out.
    /// </summary>
    private async Task<SpotifyClient> GetClientAsync(CancellationToken ct)
    {
        var tokens = await _tokens.GetTokensAsync(ProviderName, ct)
                     ?? throw new InvalidOperationException("No Spotify account connected.");

        if (tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            return new SpotifyClient(tokens.AccessToken);

        if (tokens.RefreshToken is null)
            throw new InvalidOperationException(
                "Spotify login expired. Reconnect the account in the settings.");

        _logger.LogInformation("Refreshing Spotify access token");
        var clientId = await ReadClientIdAsync(ct)
                       ?? throw new InvalidOperationException(
                           "Spotify Client ID is missing. Paste it in the account settings.");
        var refreshed = await new OAuthClient().RequestToken(
            new PKCETokenRefreshRequest(clientId, tokens.RefreshToken));
        var newTokens = ToTokens(refreshed, tokens.RefreshToken);
        await _tokens.SaveTokensAsync(ProviderName, newTokens, ct);
        return new SpotifyClient(newTokens.AccessToken);
    }

    public async Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var result = new List<UserPlaylist>();

        // /me/playlists pages through every playlist of the user, private ones
        // included (playlist-read-private scope).
        await foreach (var playlist in client.Paginate(
                           await client.Playlists.CurrentUsers()))
        {
            if (playlist.Id is null || playlist.Name is null) continue;
            result.Add(new UserPlaylist(
                playlist.Id,
                playlist.Name,
                playlist.Owner?.DisplayName ?? playlist.Owner?.Id,
                playlist.Items?.Total ?? 0,
                IsLikedSongs: false,
                playlist.Images?.FirstOrDefault()?.Url));
        }

        return result;
    }

    public async Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var tracks = new List<Track>();
        var totalDuration = TimeSpan.Zero;

        // /me/tracks pages through the whole liked library (user-library-read).
        await foreach (var saved in client.Paginate(
                           await client.Library.GetTracks()))
        {
            var track = saved.Track;
            if (track is null) continue;
            TimeSpan? duration = track.DurationMs > 0
                ? TimeSpan.FromMilliseconds(track.DurationMs)
                : null;

            tracks.Add(new Track
            {
                Title = track.Name,
                Artist = string.Join(", ",
                    (track.Artists ?? []).Select(a => a.Name)),
                Duration = duration,
                SongID = new SongID(Platform.Spotify, track.Id),
                PlatformSongID = new SongID(Platform.Spotify, track.Id),
                SourcePlatform = Platform.Spotify,
                SyncStatus = SyncStatus.Pending,
                MediaType = MediaType.MP3,
                CurrentTrackLocation = new FileLocation(
                    $"https://open.spotify.com/track/{track.Id}"),
                IsLiked = true,
            });
            if (duration is { } d) totalDuration += d;
        }

        _logger.LogInformation("Fetched Spotify liked songs: {Count}", tracks.Count);

        return new Playlist
        {
            Id = LikedId,
            Name = "Liked songs (Spotify)",
            Owner = "you",
            Description = "Your Spotify liked songs",
            SourceUrl = "spotify:liked",
            Tracks = tracks,
            TrackCount = tracks.Count,
            Duration = totalDuration,
        };
    }

    /// <summary>
    /// Fetches any playlist id through the account (private playlists work).
    /// Falls back to null when no account is connected, so the caller can
    /// try the public fetcher instead.
    /// </summary>
    public async Task<Playlist?> TryGetPlaylistViaAccountAsync(
        string playlistId, CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct)) return null;

        try
        {
            var client = await GetClientAsync(ct);
            var response = await client.Playlists.Get(playlistId);

            var tracks = new List<Track>();
            // The first page comes with the playlist; Paginate walks the rest.
            // FullPlaylist.Items is the renamed Tracks (same JSON field).
            var firstPage = response.Items
                ?? new Paging<PlaylistTrack<IPlayableItem>>();
            await foreach (var item in client.Paginate(firstPage))
            {
                if (item.Track is FullTrack track)
                {
                    TimeSpan? duration = track.DurationMs > 0
                        ? TimeSpan.FromMilliseconds(track.DurationMs)
                        : null;
                    tracks.Add(new Track
                    {
                        Title = track.Name,
                        Artist = string.Join(", ",
                            (track.Artists ?? []).Select(a => a.Name)),
                        Duration = duration,
                        SongID = new SongID(Platform.Spotify, track.Id),
                        PlatformSongID = new SongID(Platform.Spotify, track.Id),
                        SourcePlatform = Platform.Spotify,
                        SyncStatus = SyncStatus.Pending,
                        MediaType = MediaType.MP3,
                        CurrentTrackLocation = new FileLocation(
                            $"https://open.spotify.com/track/{track.Id}"),
                    });
                }
            }

            return new Playlist
            {
                Id = playlistId,
                Name = response.Name,
                Owner = response.Owner?.DisplayName ?? response.Owner?.Id,
                Description = response.Description,
                SourceUrl = $"https://open.spotify.com/playlist/{playlistId}",
                CoverImageUrl = response.Images?.FirstOrDefault()?.Url,
                Tracks = tracks,
                TrackCount = response.Items?.Total ?? tracks.Count,
                Duration = SumDurations(tracks),
            };
        }
        catch (Exception ex)
        {
            // Private playlist without access, deleted playlist, ...: the
            // public path may still work.
            _logger.LogInformation(
                "Spotify account fetch for {PlaylistId} did not work ({Message}); trying the public path",
                playlistId, ex.Message);
            return null;
        }
    }

    private Task<string?> ReadClientIdAsync(CancellationToken ct)
        => _tokens.GetSettingAsync(ProviderName, "client_id", ct);

    private static AccountTokens ToTokens(PKCETokenResponse response,
        string? keepRefreshToken = null)
        => new(
            response.AccessToken,
            // Spotify only returns a refresh token on the first login.
            response.RefreshToken ?? keepRefreshToken,
            DateTime.UtcNow + TimeSpan.FromSeconds(response.ExpiresIn));

    private static TimeSpan? SumDurations(IEnumerable<Track> tracks)
    {
        var total = TimeSpan.Zero;
        var any = false;
        foreach (var t in tracks)
        {
            if (t.Duration is not { } d) continue;
            total += d;
            any = true;
        }
        return any ? total : null;
    }
}
