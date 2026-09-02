using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using MelodyBridge.Core;
using AccountTokens = MelodyBridge.Core.AccountTokens;
using IAccountSourceProvider = MelodyBridge.Core.IAccountSourceProvider;
using Playlist = MelodyBridge.Core.Playlist;
using SongID = MelodyBridge.Core.SongID;
using Track = MelodyBridge.Core.Track;
using UserPlaylist = MelodyBridge.Core.UserPlaylist;

namespace MelodyBridge.Infrastructure.Accounts;

/// <summary>
/// YouTube account connection through the official Google OAuth code flow
/// with the read-only youtube.readonly scope. Nothing is ever written to
/// the account: private playlists and the liked-music playlist (LL) are
/// read through the YouTube Data API, which is exactly what the API is
/// for, so there is no ban risk from scraping.
///
/// The yt-dlp based public provider keeps working without any of this.
/// </summary>
public class YouTubeAccountProvider : IAccountSourceProvider
{
    public const string ProviderName = "YouTube";
    private const string LikedId = "LL";

    private readonly AccountTokenStore _tokens;
    private readonly ILogger<YouTubeAccountProvider> _logger;

    public string Name => ProviderName;

    public YouTubeAccountProvider(
        AccountTokenStore tokens,
        ILogger<YouTubeAccountProvider> logger)
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
        var clientId = await ReadClientIdAsync(ct);
        var clientSecret = await ReadClientSecretAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                "No YouTube OAuth client configured. Create OAuth credentials (type Web application) in Google Cloud Console with the YouTube Data API v3 enabled, and paste the client id and secret in the account settings.");

        var state = Guid.NewGuid().ToString("N");
        // Kept in the database: the app may restart while the user is on
        // the Google consent page, and the state must survive that.
        // Verifier stays empty: Google's flow is not PKCE.
        await _tokens.SavePendingLoginAsync(ProviderName,
            new AccountTokenStore.PendingLogin("", state, DateTime.UtcNow), ct);

        // Google's own flow builder; the redirect must be registered in the
        // Google Cloud Console exactly as used here.
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
            },
            Scopes = new[] { YouTubeService.Scope.YoutubeReadonly },
        });

        var request = flow.CreateAuthorizationCodeRequest(redirectUrl);
        request.State = state;
        var url = request.Build();
        // prompt=consent: Google then always issues a refresh token, also
        // for repeat logins, which the quiet default would skip.
        url = new UriBuilder(url)
        {
            Query = url.Query.TrimStart('?') + "&prompt=consent&access_type=offline"
        }.Uri;
        return url.ToString();
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
            throw new InvalidOperationException($"YouTube login failed: {error}");
        }

        var code = query["code"];
        var state = query["state"];
        var pending = await _tokens.GetPendingLoginAsync(ProviderName, ct);

        // Distinct messages for distinct causes, same wording scheme as
        // the Spotify provider.
        if (string.IsNullOrWhiteSpace(code) && pending is null)
        {
            _logger.LogWarning(
                "YouTube callback without a code and without a pending login; query was {Query}", redirectQuery);
            throw new InvalidOperationException(
                "YouTube sent no login code and no login was in progress. Start the login again from the accounts settings.");
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("YouTube callback without a code; query was {Query}", redirectQuery);
            throw new InvalidOperationException(
                "YouTube sent no login code back. Start the login again from the accounts settings.");
        }
        if (pending is null)
        {
            _logger.LogWarning(
                "YouTube callback arrived with no pending login left (expired, completed or the app restarted before this login was saved)");
            throw new InvalidOperationException(
                "No YouTube login was in progress, or it expired (an hour at most). Start the login again from the accounts settings.");
        }
        if (state != pending.State)
        {
            // Same reasoning as Spotify: the pending login survives a
            // mismatched answer.
            _logger.LogWarning("YouTube login state mismatch: got {Got}, expected {Expected}", state, pending.State);
            throw new InvalidOperationException(
                "This YouTube answer does not belong to the login that was started. Start the login again from the accounts settings.");
        }

        var clientId = await ReadClientIdAsync(ct);
        var clientSecret = await ReadClientSecretAsync(ct);

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
            },
            Scopes = new[] { YouTubeService.Scope.YoutubeReadonly },
        });

        try
        {
            var response = await flow.ExchangeCodeForTokenAsync(
                "melodybridge", code, redirectUrl, ct);
            await SaveFromTokenResponseAsync(response, ct);
            await _tokens.ClearPendingLoginAsync(ProviderName, ct);
        }
        catch (Exception ex)
        {
            // A failed exchange must not leave the pending state stuck:
            // the next attempt gets a fresh state value.
            await _tokens.ClearPendingLoginAsync(ProviderName, ct);
            throw new InvalidOperationException(
                $"YouTube token exchange failed: {ex.Message}. Try connecting again.");
        }

        _logger.LogInformation("YouTube account connected");
        return "YouTube account connected";
    }

    public virtual Task LogoutAsync(CancellationToken ct = default)
        => _tokens.ClearAsync(ProviderName, ct);

    public virtual Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        => _tokens.GetSettingAsync(ProviderName, key, ct);

    public virtual Task SaveSettingAsync(string key, string value, CancellationToken ct = default)
        => _tokens.SaveSettingAsync(ProviderName, key, value, ct);

    /// <summary>Google access tokens live 1h; refresh with the stored refresh token.</summary>
    private async Task<string> GetFreshAccessTokenAsync(CancellationToken ct)
    {
        var tokens = await _tokens.GetTokensAsync(ProviderName, ct)
                     ?? throw new InvalidOperationException("No YouTube account connected.");

        if (tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
            return tokens.AccessToken;

        if (tokens.RefreshToken is null)
            throw new InvalidOperationException(
                "YouTube login expired. Reconnect the account in the settings.");

        _logger.LogInformation("Refreshing YouTube access token");
        var clientId = await ReadClientIdAsync(ct);
        var clientSecret = await ReadClientSecretAsync(ct);

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = new[] { YouTubeService.Scope.YoutubeReadonly },
        });
        var refreshed = await flow.RefreshTokenAsync("melodybridge", tokens.RefreshToken, ct);
        var newTokens = new AccountTokens(
            refreshed.AccessToken,
            tokens.RefreshToken,
            DateTime.UtcNow.AddSeconds(refreshed.ExpiresInSeconds ?? 3600));
        await _tokens.SaveTokensAsync(ProviderName, newTokens, ct);
        return newTokens.AccessToken;
    }

    private async Task<YouTubeService> GetServiceAsync(CancellationToken ct)
    {
        var token = await GetFreshAccessTokenAsync(ct);
        return new YouTubeService(new BaseClientService.Initializer
        {
            // Bearer-token initializer: we manage refresh ourselves.
            HttpClientInitializer = new BearerInitializer(token),
            ApplicationName = "MelodyBridge",
        });
    }

    public async Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        using var service = await GetServiceAsync(ct);
        var result = new List<UserPlaylist>();

        // mine=true lists the channel's own playlists, private ones included.
        var request = service.Playlists.List("snippet,contentDetails,status");
        request.Mine = true;
        request.MaxResults = 50;

        string? pageToken = null;
        do
        {
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(ct);
            foreach (var playlist in response.Items ?? Enumerable.Empty<Google.Apis.YouTube.v3.Data.Playlist>())
            {
                result.Add(new UserPlaylist(
                    playlist.Id,
                    playlist.Snippet?.Title ?? "Untitled",
                    playlist.Snippet?.ChannelTitle,
                    (int)(playlist.ContentDetails?.ItemCount ?? 0),
                    playlist.Id == LikedId));
            }
            pageToken = response.NextPageToken;
        } while (pageToken is not null);

        return result;
    }

    public async Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
    {
        using var service = await GetServiceAsync(ct);

        // The channel resource carries the liked-music playlist id (LL or a
        // channel-prefixed variant).
        var channels = service.Channels.List("contentDetails,snippet");
        channels.Mine = true;
        var channel = (await channels.ExecuteAsync(ct)).Items?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Your YouTube account has no channel, so its liked songs cannot be read.");

        var likedId = channel.ContentDetails?.RelatedPlaylists?.Likes
                      ?? throw new InvalidOperationException(
                          "This channel exposes no liked playlist.");

        _logger.LogInformation("YouTube liked playlist id: {Id}", likedId);

        var tracks = new List<Track>();
        await foreach (var item in EnumeratePlaylistItemsAsync(service, likedId, ct))
        {
            var videoId = item.ContentDetails?.VideoId;
            if (string.IsNullOrWhiteSpace(videoId)) continue;

            tracks.Add(new Track
            {
                Title = item.Snippet?.Title ?? "Unknown",
                Artist = item.Snippet?.VideoOwnerChannelTitle ?? item.Snippet?.ChannelTitle ?? "Unknown",
                Duration = null, // playlistItems carry no duration
                SongID = new SongID(Platform.YouTubeMusic, videoId),
                PlatformSongID = new SongID(Platform.YouTubeMusic, videoId),
                SourcePlatform = Platform.YouTubeMusic,
                SyncStatus = SyncStatus.Pending,
                MediaType = MediaType.MP3,
                CurrentTrackLocation = new FileLocation(
                    $"https://www.youtube.com/watch?v={videoId}"),
                IsLiked = true,
            });
        }

        _logger.LogInformation("Fetched YouTube liked songs: {Count}", tracks.Count);

        return new Playlist
        {
            Id = likedId,
            Name = "Liked songs (YouTube)",
            Owner = channel.Snippet?.Title,
            Description = "Your YouTube liked songs",
            SourceUrl = likedId,
            Tracks = tracks,
            TrackCount = tracks.Count,
        };
    }

    /// <summary>
    /// Any playlist id via the Data API (private playlists work when the
    /// account owns them). Null when no account or the call fails, so the
    /// caller can fall back to the public yt-dlp path.
    /// </summary>
    public async Task<Playlist?> TryGetPlaylistViaAccountAsync(
        string playlistId, CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct)) return null;
        try
        {
            using var service = await GetServiceAsync(ct);

            var playlists = service.Playlists.List("snippet,contentDetails");
            playlists.Id = playlistId;
            var response = await playlists.ExecuteAsync(ct);
            var playlist = response.Items?.FirstOrDefault();
            if (playlist is null) return null;

            var tracks = new List<Track>();
            await foreach (var item in EnumeratePlaylistItemsAsync(service, playlistId, ct))
            {
                var videoId = item.ContentDetails?.VideoId;
                if (string.IsNullOrWhiteSpace(videoId)) continue;

                tracks.Add(new Track
                {
                    Title = item.Snippet?.Title ?? "Unknown",
                    Artist = item.Snippet?.VideoOwnerChannelTitle
                             ?? item.Snippet?.ChannelTitle ?? "Unknown",
                    Duration = null,
                    SongID = new SongID(Platform.YouTubeMusic, videoId),
                    PlatformSongID = new SongID(Platform.YouTubeMusic, videoId),
                    SourcePlatform = Platform.YouTubeMusic,
                    SyncStatus = SyncStatus.Pending,
                    MediaType = MediaType.MP3,
                    CurrentTrackLocation = new FileLocation(
                        $"https://www.youtube.com/watch?v={videoId}"),
                });
            }

            return new Playlist
            {
                Id = playlistId,
                Name = playlist.Snippet?.Title ?? "Unknown playlist",
                Owner = playlist.Snippet?.ChannelTitle,
                Description = playlist.Snippet?.Description,
                SourceUrl = $"https://www.youtube.com/playlist?list={playlistId}",
                Tracks = tracks,
                TrackCount = tracks.Count,
            };
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "YouTube account fetch for {PlaylistId} did not work ({Message}); trying the public path",
                playlistId, ex.Message);
            return null;
        }
    }

    private static async IAsyncEnumerable<Google.Apis.YouTube.v3.Data.PlaylistItem> EnumeratePlaylistItemsAsync(
        YouTubeService service, string playlistId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var request = service.PlaylistItems.List("snippet,contentDetails");
        request.PlaylistId = playlistId;
        request.MaxResults = 50;

        string? pageToken = null;
        do
        {
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(ct);
            foreach (var item in response.Items ?? Enumerable.Empty<Google.Apis.YouTube.v3.Data.PlaylistItem>())
                yield return item;
            pageToken = response.NextPageToken;
        } while (pageToken is not null);
    }

    private Task<string?> ReadClientIdAsync(CancellationToken ct)
        => _tokens.GetSettingAsync(ProviderName, "client_id", ct);

    private Task<string?> ReadClientSecretAsync(CancellationToken ct)
        => _tokens.GetSettingAsync(ProviderName, "client_secret", ct);

    private Task SaveFromTokenResponseAsync(TokenResponse response, CancellationToken ct)
        => _tokens.SaveTokensAsync(ProviderName, new AccountTokens(
            response.AccessToken,
            response.RefreshToken,
            response.ExpiresInSeconds is > 0
                ? DateTime.UtcNow.AddSeconds(response.ExpiresInSeconds.Value)
                : DateTime.UtcNow.AddHours(1)), ct);
}

/// <summary>Adds the OAuth bearer token to every Data API request.</summary>
internal sealed class BearerInitializer : Google.Apis.Http.IConfigurableHttpClientInitializer
{
    private readonly string _token;
    public BearerInitializer(string token) => _token = token;

    public void Initialize(Google.Apis.Http.ConfigurableHttpClient httpClient)
        => httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
}
