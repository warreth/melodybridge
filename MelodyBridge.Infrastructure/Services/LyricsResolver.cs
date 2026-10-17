using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Resolves plain lyrics for a track from LRCLIB (lrclib.net), a free
/// public lyrics database with no account. One lookup per track, cached
/// including the misses so a track without lyrics is not asked again on
/// every run. Failures (timeout, 5xx, malformed answer) are never cached:
/// they may recover, and they never fail a download.
/// </summary>
public class LyricsResolver
{
    private const int MaxLyricsChars = 20_000;
    private const int MaxCacheEntries = 512;

    private readonly HttpClient _http;
    private readonly ILogger<LyricsResolver> _logger;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    // One request at a time with a small gap: a re-tag pass over a library
    // would otherwise hammer a free community service.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <summary>Per-request budget; internal so tests can shrink it.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public LyricsResolver(HttpClient http, ILogger<LyricsResolver> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Plain lyrics, or null when there are none (or nothing answered).</summary>
    public async Task<string?> ResolveAsync(
        string? artist, string? title, string? album = null,
        TimeSpan? duration = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;

        var key = string.Join('|', artist.Trim().ToLowerInvariant(),
            title.Trim().ToLowerInvariant(), album?.Trim().ToLowerInvariant(),
            duration is { TotalSeconds: > 0 } d ? ((long)Math.Round(d.TotalSeconds)).ToString() : "");
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            var url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(artist.Trim())
                + "&track_name=" + Uri.EscapeDataString(title.Trim());
            if (!string.IsNullOrWhiteSpace(album))
                url += "&album_name=" + Uri.EscapeDataString(album.Trim());
            if (duration is { TotalSeconds: > 0 } given)
                url += "&duration=" + (long)Math.Round(given.TotalSeconds);

            await _gate.WaitAsync(cts.Token);
            HttpResponseMessage response;
            try
            {
                var wait = MinInterval - (DateTimeOffset.UtcNow - _lastRequest);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                _lastRequest = DateTimeOffset.UtcNow;
                response = await _http.GetAsync(url, cts.Token);
            }
            finally
            {
                _gate.Release();
            }
            using var _ = response;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A definitive "no lyrics for this track": remember it.
                Cache(key, null);
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Lyrics lookup answered {Status} for {Artist} - {Title}",
                    (int)response.StatusCode, artist, title);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cts.Token));
            var lyrics = doc.RootElement.TryGetProperty("plainLyrics", out var element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(lyrics))
            {
                Cache(key, null);
                return null;
            }

            if (lyrics.Length > MaxLyricsChars) lyrics = lyrics[..MaxLyricsChars];
            Cache(key, lyrics);
            return lyrics;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lyrics lookup failed for {Artist} - {Title}", artist, title);
            return null;
        }
    }

    private void Cache(string key, string? lyrics)
    {
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[key] = lyrics;
    }
}
