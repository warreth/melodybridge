using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Deezer;

/// <summary>
/// Deezer's public search API, no account needed: the plugin resolves a
/// track reference from metadata, then hands the track URL to the deemix
/// CLI, which owns the login (ARL) and the actual download. Search
/// results carry an ISRC, which narrows the pool to the exact recording.
///
/// Verified against the live API.
/// </summary>
public class DeezerClient
{
    private const string SearchBase = "https://api.deezer.com/search";

    private readonly HttpClient _http;
    private readonly ILogger<DeezerClient> _logger;

    public DeezerClient(HttpClient http, ILogger<DeezerClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>One public search result.</summary>
    public record DeezerTrack(
        long Id, string Title, string? Artist, TimeSpan? Duration, string? Isrc);

    /// <summary>Tracks matching the query, best-effort parsed.</summary>
    public async Task<IReadOnlyList<DeezerTrack>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        try
        {
            var url = $"{SearchBase}?q={Uri.EscapeDataString(query)}&limit={limit}";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deezer search returned {Status}", (int)response.StatusCode);
                return Array.Empty<DeezerTrack>();
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return Array.Empty<DeezerTrack>();

            var results = new List<DeezerTrack>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var trackId))
                    continue;

                var duration = item.TryGetProperty("duration", out var d) &&
                               d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetInt32())
                    : (TimeSpan?)null;

                var artist = item.TryGetProperty("artist", out var a) &&
                             a.ValueKind == JsonValueKind.Object
                    ? GetString(a, "name")
                    : null;

                results.Add(new DeezerTrack(
                    trackId,
                    GetString(item, "title") ?? "",
                    artist,
                    duration,
                    GetString(item, "isrc")));
            }
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deezer search failed");
            return Array.Empty<DeezerTrack>();
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
