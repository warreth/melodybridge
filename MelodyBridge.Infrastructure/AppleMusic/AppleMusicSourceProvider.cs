using System.Net;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.AppleMusic;

/// <summary>
/// Public Apple Music playlist source. The API path reads full metadata
/// including ISRC through the same public web token a browser uses. The page
/// parser is the independent fallback when that token or the API fails, so a
/// public or shared playlist keeps working without any account.
/// </summary>
public sealed class AppleMusicSourceProvider : ISourceProvider
{
    /// <summary>Apple caps a relationship page at 300 items.</summary>
    internal const int PageSize = 300;

    /// <summary>Runaway guard: a playlist longer than this is not a personal list.</summary>
    public const int MaxTracks = 20_000;

    private const int MaxPages = 100;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    private readonly AppleMusicApiClient _api;
    private readonly HttpClient _pageHttp;
    private readonly ILogger<AppleMusicSourceProvider> _logger;

    public string Name => "Apple Music";
    public Platform Platform => Platform.AppleMusic;

    public AppleMusicSourceProvider(
        AppleMusicApiClient api,
        ILogger<AppleMusicSourceProvider> logger,
        HttpClient? pageHttp = null)
    {
        _api = api;
        _logger = logger;
        _pageHttp = pageHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
    }

    public bool CanHandle(string sourceIdentifier)
        => AppleMusicUrls.CanHandle(sourceIdentifier);

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var reference = AppleMusicUrls.Parse(sourceIdentifier);
        if (reference.NeedsAccount)
            throw new InvalidOperationException(
                "That Apple Music playlist lives in a library. Connect an Apple Music "
                + "web session in Settings, or paste a shared playlist link instead.");

        var canonicalUrl = AppleMusicUrls.CanonicalUrl(reference.Storefront, reference.Id);

        try
        {
            var viaApi = await FetchViaApiAsync(reference, canonicalUrl);
            if (viaApi.Tracks is { Count: > 0 })
            {
                _logger.LogInformation(
                    "Fetched Apple Music playlist '{Name}' with {Count} tracks through the API",
                    viaApi.Name, viaApi.TrackCount);
                return viaApi;
            }

            _logger.LogWarning(
                "Apple Music API returned no tracks for {PlaylistId}; trying the public page",
                reference.Id);
        }
        catch (AppleMusicApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex) when (IsFallbackWorthy(ex))
        {
            _logger.LogWarning(ex,
                "Apple Music API fetch for {PlaylistId} failed; trying the public page",
                reference.Id);
        }

        var playlist = await FetchViaPageAsync(reference, canonicalUrl);
        _logger.LogInformation(
            "Fetched Apple Music playlist '{Name}' with {Count} tracks through the public page",
            playlist.Name, playlist.TrackCount);
        return playlist;
    }

    private async Task<Playlist> FetchViaApiAsync(
        AppleMusicPlaylistRef reference, string canonicalUrl)
    {
        var path = $"/v1/catalog/{reference.Storefront}/playlists/{reference.Id}"
                   + $"?include=tracks&limit[tracks]={PageSize}";
        using var first = await _api.GetAsync(path);
        var playlist = AppleMusicMapper.MapPlaylist(
            first.RootElement, reference.Id, canonicalUrl);

        var next = AppleMusicMapper.NextLink(first.RootElement);
        var pages = 0;
        while (!string.IsNullOrWhiteSpace(next)
               && playlist.Tracks is { Count: < MaxTracks }
               && pages++ < MaxPages)
        {
            using var page = await _api.GetAsync(next);
            playlist.Tracks ??= new List<Track>();
            playlist.Tracks.AddRange(AppleMusicMapper.MapTracks(page.RootElement));
            playlist.TrackCount = playlist.Tracks.Count;
            playlist.Duration = SumDurations(playlist.Tracks);
            next = AppleMusicMapper.NextLink(page.RootElement);
        }

        if (playlist.Tracks is { Count: > MaxTracks } over)
        {
            playlist.Tracks = over.Take(MaxTracks).ToList();
            playlist.TrackCount = playlist.Tracks.Count;
            playlist.Warning = $"playlist truncated at {MaxTracks} tracks";
        }

        return playlist;
    }

    private async Task<Playlist> FetchViaPageAsync(
        AppleMusicPlaylistRef reference, string canonicalUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, canonicalUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var response = await _pageHttp.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "Apple Music did not find that playlist. It must be public or shared with a link.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Apple Music's public page answered {(int)response.StatusCode}. "
                + "The playlist must be public or shared with a link.");

        var html = await response.Content.ReadAsStringAsync();
        return AppleMusicPageParser.Parse(html, reference.Id, canonicalUrl);
    }

    private static bool IsFallbackWorthy(Exception ex) => ex switch
    {
        AppleMusicTokenException => true,
        AppleMusicApiException api => api.StatusCode is
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests
            || (int)api.StatusCode >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,
        JsonException => true,
        _ => false,
    };

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
