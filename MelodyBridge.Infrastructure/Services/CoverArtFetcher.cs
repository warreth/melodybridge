using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Fetches cover art bytes for a track. One album usually shares one URL,
/// so a success is cached and an album costs one request. Failures are
/// never fatal and never cached, so a hiccup recovers on the next download.
/// Hard limits keep this safe to call from the download path: http(s) only,
/// a short timeout, and a size cap.
/// </summary>
public class CoverArtFetcher
{
    private const int MaxBytes = 10 * 1024 * 1024;
    private const int MaxCacheEntries = 64;
    private const int MaxCachedEntryBytes = 4 * 1024 * 1024;
    private const long MaxCacheBytes = 64L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<CoverArtFetcher> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private long _cachedBytes;

    /// <summary>Per-request budget; internal so tests can shrink it.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public CoverArtFetcher(HttpClient http, ILogger<CoverArtFetcher> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>The cover bytes, or null for anything that is not a usable image.</summary>
    public async Task<byte[]?> FetchAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        if (_cache.TryGetValue(url, out var cached)) return cached;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Cover fetch answered {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Cover fetch answered {Type}, not an image, for {Url}", mediaType, url);
                return null;
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            {
                _logger.LogDebug("Cover at {Url} is {Bytes} bytes, over the cap", url, length);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var bytes = await ReadCappedAsync(stream, MaxBytes, cts.Token);
            if (bytes is null || bytes.Length == 0) return null;

            Remember(url, bytes);
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cover fetch failed for {Url}", url);
            return null;
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; null when the body is bigger.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private void Remember(string url, byte[] bytes)
    {
        // Very large artwork is not worth caching (and would dominate the
        // budget); it is still embedded for this track.
        if (bytes.Length > MaxCachedEntryBytes) return;

        if (_cache.Count >= MaxCacheEntries
            || Interlocked.Read(ref _cachedBytes) + bytes.Length > MaxCacheBytes)
        {
            _cache.Clear();
            Interlocked.Exchange(ref _cachedBytes, 0);
        }

        if (_cache.TryAdd(url, bytes))
            Interlocked.Add(ref _cachedBytes, bytes.Length);
    }
}
