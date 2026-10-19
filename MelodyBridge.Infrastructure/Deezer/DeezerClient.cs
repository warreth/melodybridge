using System.Net;
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

    /// <summary>How an ARL check ended.</summary>
    public enum DeezerArlStatus
    {
        /// <summary>The ARL is signed in to a Deezer account.</summary>
        Valid,

        /// <summary>Deezer refused the ARL or the session is logged out.</summary>
        Invalid,

        /// <summary>The check could not run; the ARL may well be fine.</summary>
        Unreachable,
    }

    /// <summary>
    /// The account behind an ARL. <paramref name="Lossless"/> is null when
    /// Deezer did not report it; false means FLAC requests will fall back.
    /// </summary>
    public record DeezerArlCheck(
        DeezerArlStatus Status,
        string? Message = null,
        long? UserId = null,
        bool? Lossless = null);

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

    /// <summary>
    /// Checks the ARL against Deezer's own web session endpoint. This is
    /// the same login deemix uses, so an ARL that is logged out here will
    /// fail there too; a Cloudflare or network answer is reported as
    /// unreachable instead of blaming the ARL.
    /// </summary>
    public async Task<DeezerArlCheck> ValidateArlAsync(
        string arl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(arl))
            return new(DeezerArlStatus.Invalid, "No Deezer ARL is configured");

        const string url =
            "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData"
            + "&input=3&api_version=1.0&api_token=null";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Cookie", "arl=" + arl.Trim());
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogDebug("Deezer refused the ARL ({Status})", (int)response.StatusCode);
                return new(DeezerArlStatus.Invalid,
                    "Deezer refused the ARL; paste a fresh one from deezer.com");
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deezer ARL check answered {Status}", (int)response.StatusCode);
                return new(DeezerArlStatus.Unreachable,
                    $"Deezer answered HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Array &&
                error.GetArrayLength() > 0)
            {
                return new(DeezerArlStatus.Invalid,
                    "The Deezer ARL is no longer signed in; paste a fresh one");
            }

            if (!root.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("USER", out var user))
                return new(DeezerArlStatus.Unreachable,
                    "Deezer returned an unexpected answer");

            long? userId = user.TryGetProperty("USER_ID", out var id)
                           && id.ValueKind == JsonValueKind.Number
                           && id.TryGetInt64(out var parsed)
                ? parsed
                : null;

            if (userId is null or <= 0)
            {
                _logger.LogDebug("Deezer ARL check found no signed-in user");
                return new(DeezerArlStatus.Invalid,
                    "The Deezer ARL is no longer signed in; paste a fresh one");
            }

            bool? lossless = null;
            if (user.TryGetProperty("OPTIONS", out var options) &&
                options.TryGetProperty("web_lossless", out var webLossless) &&
                (webLossless.ValueKind == JsonValueKind.True
                 || webLossless.ValueKind == JsonValueKind.False))
            {
                lossless = webLossless.ValueKind == JsonValueKind.True;
            }

            return new(DeezerArlStatus.Valid, null, userId, lossless);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deezer ARL check failed");
            return new(DeezerArlStatus.Unreachable,
                "Deezer could not be reached to check the ARL");
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
