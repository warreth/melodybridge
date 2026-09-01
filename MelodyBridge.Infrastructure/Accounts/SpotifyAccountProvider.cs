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

    // One in-flight login: the PKCE verifier + state must survive between
    // Begin and Complete. Single-user app, one login at a time is enough.
    private static (string Verifier, string State)? _pendingLogin;

    public string Name => ProviderName;

    public SpotifyAccountProvider(
        AccountTokenStore tokens,
        ILogger<SpotifyAccountProvider> logger)
    {
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        var tokens = await _tokens.GetTokensAsync(ProviderName, ct);
        return tokens is { AccessToken.Length: > 0 };
    }

    public async Task<string> BeginLoginAsync(string redirectUrl, CancellationToken ct = default)
    {
        // The user's own app (or MelodyBridge's default), set in Settings.
        var clientId = await ReadClientIdAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "No Spotify client id configured. Create an app at developer.spotify.com and paste its Client ID in the account settings.");

        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        var state = Guid.NewGuid().ToString("N");
        _pendingLogin = (verifier, state);

        var login = new LoginRequest(
            new Uri(redirectUrl),
            clientId,
            LoginRequest.ResponseType.Code)
        {
            CodeChallengeMethod = "S256",
            CodeChallenge = challenge,
            Scope = Scopes,
        };
        return login.ToUri().ToString();
    }

    public async Task<string> CompleteLoginAsync(
        string redirectQuery, string redirectUrl, CancellationToken ct = default)
    {
        var query = System.Web.HttpUtility.ParseQueryString(
            redirectQuery.StartsWith('?') ? redirectQuery[1..] : redirectQuery);

        var error = query["error"];
        if (!string.IsNullOrWhiteSpace(error))
        {
            _pendingLogin = null;
            throw new InvalidOperationException($"Spotify login failed: {error}");
        }

        var code = query["code"];
        var state = query["state"];
        var pending = _pendingLogin;
        _pendingLogin = null;

        if (string.IsNullOrWhiteSpace(code) || pending is null)
            throw new InvalidOperationException("Spotify login expired or was cancelled. Try again.");
        if (state != pending.Value.State)
            throw new InvalidOperationException(
                "Spotify login did not check out (state mismatch). Try again.");

        var clientId = await ReadClientIdAsync(ct);
        var response = await new OAuthClient().RequestToken(
            new PKCETokenRequest(clientId, code, new Uri(redirectUrl), pending.Value.Verifier));

        await _tokens.SaveTokensAsync(ProviderName, ToTokens(response), ct);
        _logger.LogInformation("Spotify account connected");
        return "Spotify account connected";
    }

    public Task LogoutAsync(CancellationToken ct = default)
        => _tokens.ClearAsync(ProviderName, ct);

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => _tokens.GetSettingAsync(ProviderName, key, ct);

    public Task SaveSettingAsync(string key, string value, CancellationToken ct = default)
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
        var clientId = await ReadClientIdAsync(ct);
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
            result.Add(new UserPlaylist(
                playlist.Id,
                playlist.Name,
                playlist.Owner?.DisplayName ?? playlist.Owner?.Id,
                playlist.Tracks?.Total ?? 0,
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
            await foreach (var item in client.Paginate(response.Tracks!))
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
                TrackCount = response.Tracks?.Total ?? tracks.Count,
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
