using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// The read-only half of Tidal's public v1 API that the plugin needs:
/// searching the catalogue with the user's own tiddl session. Verified
/// against the client tiddl itself ships (GET /v1/search with a bearer
/// token and a country code), so the search speaks exactly what the
/// download tool speaks.
/// </summary>
public class TidalClient
{
    /// <summary>One search result, already reduced to what ranking needs.</summary>
    public record TidalTrack(
        long Id,
        string Title,
        string Artist,
        TimeSpan Duration,
        string? Isrc,
        bool StreamReady);

    private readonly HttpClient _http;
    private readonly ILogger<TidalClient> _logger;

    public TidalClient(HttpClient http, ILogger<TidalClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Track search. An unauthorized answer (expired or rejected token) is
    /// reported as an empty result: the plugin's availability check is the
    /// place that explains a bad login, not every search.
    /// </summary>
    public async Task<IReadOnlyList<TidalTrack>> SearchTracksAsync(
        string query, int limit, string token, string countryCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(token))
            return Array.Empty<TidalTrack>();

        var url = "https://api.tidal.com/v1/search?countryCode="
            + Uri.EscapeDataString(string.IsNullOrWhiteSpace(countryCode) ? "US" : countryCode)
            + "&types=TRACKS&limit=" + Math.Clamp(limit, 1, 50)
            + "&query=" + Uri.EscapeDataString(query);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tidal search answered {Status}", (int)response.StatusCode);
                return Array.Empty<TidalTrack>();
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return ParseSearch(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tidal search failed");
            return Array.Empty<TidalTrack>();
        }
    }

    /// <summary>The search payload's track items. Internal for tests.</summary>
    internal static IReadOnlyList<TidalTrack> ParseSearch(JsonElement root)
    {
        var tracks = new List<TidalTrack>();
        if (!root.TryGetProperty("tracks", out var tracksElement)
            || !tracksElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return tracks;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = item.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out var parsedId)
                ? parsedId
                : 0;
            if (id <= 0) continue;

            var title = item.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? ""
                : "";
            if (title.Length == 0) continue;

            var duration = item.TryGetProperty("duration", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetInt32(out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;

            var isrc = item.TryGetProperty("isrc", out var isrcElement)
                && isrcElement.ValueKind == JsonValueKind.String
                ? isrcElement.GetString()
                : null;

            var streamReady = !item.TryGetProperty("streamReady", out var readyElement)
                || readyElement.ValueKind != JsonValueKind.False;

            tracks.Add(new TidalTrack(
                id, title, ArtistOf(item), duration ?? TimeSpan.Zero, isrc, streamReady));
        }
        return tracks;
    }

    /// <summary>The first credited artist name; Tidal lists them as objects.</summary>
    private static string ArtistOf(JsonElement item)
    {
        if (item.TryGetProperty("artists", out var artists)
            && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artists.EnumerateArray())
            {
                if (artist.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString()!;
            }
        }

        if (item.TryGetProperty("artist", out var single)
            && single.ValueKind == JsonValueKind.Object
            && single.TryGetProperty("name", out var singleName)
            && singleName.ValueKind == JsonValueKind.String)
            return singleName.GetString() ?? "";

        return "";
    }
}
