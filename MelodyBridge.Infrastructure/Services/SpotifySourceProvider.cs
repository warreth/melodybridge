using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MelodyBridge.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Public Spotify playlist source provider.
///
/// This intentionally does not require a Spotify account, OAuth app, API key,
/// or cookies. It reads public playlist data from Spotify's public embed page
/// and parses the embedded Next.js JSON payload. An optional Spotify cookie can
/// be configured later for account playlist discovery/fallback requests, but
/// public playlist scraping works without it.
/// </summary>
public partial class SpotifySourceProvider : ISourceProvider
{
    private readonly ILogger<SpotifySourceProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _spotifyCookie;

    private const string OpenSpotifyBase = "https://open.spotify.com";

    public string Name => "Spotify";
    public Platform Platform => Platform.Spotify;

    public SpotifySourceProvider(
        ILogger<SpotifySourceProvider> logger,
        IConfiguration? configuration = null)
        : this(logger, configuration, null) { }

    /// <summary>Internal constructor for unit testing with a mock HttpClient.</summary>
    internal SpotifySourceProvider(
        ILogger<SpotifySourceProvider> logger,
        IConfiguration? configuration,
        HttpClient? httpClient)
    {
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        ConfigureDefaultHeaders(_httpClient);

        _spotifyCookie = configuration?["Spotify:Cookie"];
        if (string.IsNullOrWhiteSpace(_spotifyCookie))
            _spotifyCookie = Environment.GetEnvironmentVariable("SPOTIFY_COOKIE");
    }

    /// <summary>
    /// Public playlist scraping does not require credentials.
    /// </summary>
    public bool HasCredentials => true;

    /// <summary>
    /// Fetches a public Spotify playlist by URL, URI, or playlist ID and returns
    /// the playlist metadata plus all tracks exposed by Spotify's public embed.
    /// </summary>
    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var playlistId = ExtractPlaylistId(sourceIdentifier);
        var canonicalUrl = $"{OpenSpotifyBase}/playlist/{playlistId}";

        _logger.LogInformation("Scraping public Spotify playlist: {PlaylistId}", playlistId);

        var embedUrl = $"{OpenSpotifyBase}/embed/playlist/{playlistId}?utm_source=generator";
        var embedHtml = await GetStringAsync(embedUrl);
        var playlist = ParseEmbedPlaylistHtml(embedHtml, playlistId, canonicalUrl);

        if (playlist is null || playlist.Tracks is null || playlist.Tracks.Count == 0)
        {
            _logger.LogWarning("Spotify embed page did not include tracks for {PlaylistId}; trying public playlist page", playlistId);
            var publicHtml = await GetStringAsync($"{canonicalUrl}?nd=1");
            playlist = ParseEmbedPlaylistHtml(publicHtml, playlistId, canonicalUrl)
                       ?? ParseVisiblePlaylistHtml(publicHtml, playlistId, canonicalUrl);
        }

        if (playlist is null || playlist.Tracks is null || playlist.Tracks.Count == 0)
        {
            var oEmbed = await TryFetchOEmbedAsync(canonicalUrl);
            throw new InvalidOperationException(
                $"Could not scrape any tracks from public Spotify playlist '{playlistId}'" +
                (oEmbed?.Name is { Length: > 0 } ? $" ('{oEmbed.Name}')" : string.Empty) +
                ". The playlist must be public and Spotify must expose tracks in the public embed page.");
        }

        if (playlist.TrackCount is null || playlist.TrackCount == 0)
            playlist.TrackCount = playlist.Tracks.Count;
        if (playlist.Duration is null)
            playlist.Duration = SumDurations(playlist.Tracks);

        // oEmbed is a useful lightweight metadata fallback for title/cover.
        var metadata = await TryFetchOEmbedAsync(canonicalUrl);
        if (metadata is not null)
        {
            playlist.Name ??= metadata.Name;
            playlist.CoverImageUrl ??= metadata.CoverImageUrl;
        }

        _logger.LogInformation(
            "Scraped Spotify playlist '{Name}' ({PlaylistId}) with {TrackCount} tracks",
            playlist.Name, playlist.Id, playlist.Tracks.Count);

        return playlist;
    }

    public Task<string?> ResolveTrackUrlAsync(string query)
    {
        if (!string.IsNullOrWhiteSpace(query) &&
            query.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            query.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(query);
        }

        // Public page scraping cannot reliably perform Spotify search without
        // account/API access. Downstream music providers should search by
        // artist/title from the scraped playlist tracks instead.
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Basic public/user playlist URL discovery. This can optionally use
    /// Spotify:Cookie / SPOTIFY_COOKIE for accounts or pages that require a
    /// logged-in browser session. It returns public playlist URLs found in the
    /// user's Spotify web page HTML without calling the Spotify Web API.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUserPlaylistUrlsAsync(string userIdentifier)
    {
        var userId = ExtractUserId(userIdentifier);
        var html = await GetStringAsync($"{OpenSpotifyBase}/user/{Uri.EscapeDataString(userId)}/playlists?nd=1");
        return ExtractPlaylistUrls(html).ToArray();
    }

    /// <summary>
    /// Extracts the Spotify playlist ID from a public URL, embed URL, URI, or raw ID.
    /// </summary>
    public static string ExtractPlaylistId(string sourceIdentifier)
    {
        if (string.IsNullOrWhiteSpace(sourceIdentifier))
            throw new ArgumentException("Playlist identifier cannot be empty", nameof(sourceIdentifier));

        var playlistMatch = PlaylistIdRegex().Match(sourceIdentifier.Trim());
        if (playlistMatch.Success)
            return playlistMatch.Groups[1].Value;

        if (SpotifyIdRegex().IsMatch(sourceIdentifier.Trim()))
            return sourceIdentifier.Trim();

        throw new ArgumentException(
            $"Could not extract Spotify playlist ID from: '{sourceIdentifier}'. " +
            "Expected https://open.spotify.com/playlist/..., /embed/playlist/..., spotify:playlist:..., or the playlist ID.");
    }

    public static string ExtractUserId(string userIdentifier)
    {
        if (string.IsNullOrWhiteSpace(userIdentifier))
            throw new ArgumentException("Spotify user identifier cannot be empty", nameof(userIdentifier));

        var match = UserIdRegex().Match(userIdentifier.Trim());
        if (match.Success)
            return WebUtility.UrlDecode(match.Groups[1].Value);

        return userIdentifier.Trim();
    }

    public static IReadOnlyList<string> ExtractPlaylistUrls(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<string>();

        var urls = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PlaylistHrefRegex().Matches(html))
        {
            var id = match.Groups[1].Value;
            urls.Add($"{OpenSpotifyBase}/playlist/{id}");
        }

        return urls.ToArray();
    }

    public static Playlist? ParseEmbedPlaylistHtml(string html, string playlistId, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var json = ExtractNextDataJson(html);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        if (!TryGetEntity(doc.RootElement, out var entity))
            return null;

        return ParseEntity(entity, playlistId, sourceUrl);
    }

    public static Playlist? ParseVisiblePlaylistHtml(string html, string playlistId, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var title = FirstGroup(html, @"<h1[^>]*>(.*?)</h1>")
                ?? FirstGroup(html, @"title=""Spotify Embed:\s*([^""]+)""")
                    ?? "Spotify Playlist";

        var owner = FirstGroup(html, @"<span[^>]*class=""[^""]*subtitle[^""]*""[^>]*>(.*?)</span>");
        var tracks = new List<Track>();

        foreach (Match row in VisibleTrackRowRegex().Matches(html))
        {
            var rowHtml = row.Value;
            var trackTitle = CleanHtml(FirstGroup(rowHtml, @"<h3[^>]*>(.*?)</h3>") ?? string.Empty);
            var artist = CleanHtml(FirstGroup(rowHtml, @"<h4[^>]*>(.*?)</h4>") ?? "Unknown");
            var duration = ParseDuration(FirstGroup(rowHtml, @"data-testid=""duration-cell""[^>]*>(.*?)</div>"));

            if (string.IsNullOrWhiteSpace(trackTitle))
                continue;

            tracks.Add(CreateTrack(
                id: $"spotify-public-{tracks.Count + 1}",
                title: trackTitle,
                artist: artist,
                duration: duration,
                spotifyUrl: $"{OpenSpotifyBase}/playlist/{playlistId}#track-{tracks.Count + 1}"));
        }

        if (tracks.Count == 0)
            return null;

        return new Playlist
        {
            Id = playlistId,
            Name = CleanHtml(title),
            Owner = CleanHtml(owner),
            SourceUrl = sourceUrl,
            TrackCount = tracks.Count,
            Duration = SumDurations(tracks),
            Tracks = tracks,
        };
    }

    private static Playlist ParseEntity(JsonElement entity, string fallbackPlaylistId, string sourceUrl)
    {
        var id = GetString(entity, "id") ?? fallbackPlaylistId;
        var name = GetString(entity, "name") ?? GetString(entity, "title") ?? "Spotify Playlist";
        var owner = GetString(entity, "subtitle");
        var description = GetString(entity, "description");
        var cover = TryGetCoverArtUrl(entity);

        var tracks = new List<Track>();
        if (entity.TryGetProperty("trackList", out var trackList) && trackList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in trackList.EnumerateArray())
            {
                var uri = GetString(item, "uri");
                var trackId = ExtractTrackId(uri) ?? GetString(item, "uid");
                var title = GetString(item, "title");
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var artist = NormalizeArtists(GetString(item, "subtitle"));
                var durationMs = item.TryGetProperty("duration", out var durationProp) && durationProp.TryGetInt32(out var ms)
                    ? ms
                    : 0;

                tracks.Add(CreateTrack(
                    trackId ?? $"spotify-public-{tracks.Count + 1}",
                    title,
                    artist,
                    durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : null,
                    trackId is { Length: > 0 } ? $"{OpenSpotifyBase}/track/{trackId}" : sourceUrl));
            }
        }

        return new Playlist
        {
            Id = id,
            Name = name,
            Owner = NormalizeArtists(owner),
            Description = description,
            SourceUrl = sourceUrl,
            CoverImageUrl = cover,
            TrackCount = tracks.Count,
            Duration = SumDurations(tracks),
            Tracks = tracks,
        };
    }

    private static Track CreateTrack(string id, string title, string artist, TimeSpan? duration, string spotifyUrl)
    {
        return new Track
        {
            Title = title,
            Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist,
            Duration = duration,
            SongID = new SongID(Platform.Spotify, id),
            PlatformSongID = new SongID(Platform.Spotify, id),
            Quality = null,
            SourcePlatform = Platform.Spotify,
            SyncStatus = SyncStatus.Pending,
            MediaType = MediaType.MP3,
            CurrentTrackLocation = new FileLocation(spotifyUrl),
        };
    }

    private async Task<string> GetStringAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_spotifyCookie))
            request.Headers.TryAddWithoutValidation("Cookie", _spotifyCookie);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<SpotifyOEmbedMetadata?> TryFetchOEmbedAsync(string playlistUrl)
    {
        try
        {
            var url = $"{OpenSpotifyBase}/oembed?url={HttpUtility.UrlEncode(playlistUrl)}";
            var body = await GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new SpotifyOEmbedMetadata(
                GetString(root, "title"),
                GetString(root, "thumbnail_url"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Spotify oEmbed metadata fetch failed for {PlaylistUrl}", playlistUrl);
            return null;
        }
    }

    private static void ConfigureDefaultHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }

        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
    }

    private static string? ExtractNextDataJson(string html)
    {
        var match = NextDataRegex().Match(html);
        if (!match.Success)
            return null;

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static bool TryGetEntity(JsonElement root, out JsonElement entity)
    {
        entity = default;
        return root.TryGetProperty("props", out var props)
               && props.TryGetProperty("pageProps", out var pageProps)
               && pageProps.TryGetProperty("state", out var state)
               && state.TryGetProperty("data", out var data)
               && data.TryGetProperty("entity", out entity)
               && entity.ValueKind == JsonValueKind.Object;
    }

    private static string? TryGetCoverArtUrl(JsonElement entity)
    {
        if (!entity.TryGetProperty("coverArt", out var coverArt) ||
            !coverArt.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var source in sources.EnumerateArray())
        {
            var url = GetString(source, "url");
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        return null;
    }

    private static string? ExtractTrackId(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var match = TrackIdRegex().Match(uri);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static string NormalizeArtists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        return value.Replace('\u00a0', ' ').Trim();
    }

    private static string? FirstGroup(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string CleanHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var noTags = Regex.Replace(value, "<.*?>", string.Empty, RegexOptions.Singleline);
        return WebUtility.HtmlDecode(noTags).Replace('\u00a0', ' ').Trim();
    }

    private static TimeSpan? ParseDuration(string? value)
    {
        value = CleanHtml(value);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split(':').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return parts.Length switch
        {
            2 => new TimeSpan(0, 0, parts[0], parts[1]),
            3 => new TimeSpan(0, parts[0], parts[1], parts[2]),
            _ => null,
        };
    }

    private static TimeSpan SumDurations(IEnumerable<Track> tracks)
    {
        return tracks.Aggregate(TimeSpan.Zero, (total, track) => total + (track.Duration ?? TimeSpan.Zero));
    }

    private sealed record SpotifyOEmbedMetadata(string? Name, string? CoverImageUrl);

    [GeneratedRegex(@"(?:open\.spotify\.com/(?:embed/)?playlist/|spotify:playlist:)([A-Za-z0-9]{22,})", RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"(?:open\.spotify\.com/user/|spotify:user:)([^/?#:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UserIdRegex();

    [GeneratedRegex(@"(?:href=[""'](?:https://open\.spotify\.com)?/playlist/|spotify:playlist:)([A-Za-z0-9]{22,})", RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistHrefRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]{22,}$")]
    private static partial Regex SpotifyIdRegex();

    [GeneratedRegex(@"spotify:track:([A-Za-z0-9]{22,})", RegexOptions.IgnoreCase)]
    private static partial Regex TrackIdRegex();

    [GeneratedRegex(@"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    [GeneratedRegex(@"<li[^>]+data-testid=[""']tracklist-row-\d+[""'][^>]*>.*?</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VisibleTrackRowRegex();
}
