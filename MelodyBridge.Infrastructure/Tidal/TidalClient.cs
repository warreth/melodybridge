using System.Net;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// The read-only Tidal v1 API check the plugin needs: one search request
/// against the user's own tiddl session to tell a valid token from an
/// expired or rejected one. The catalogue search itself lives in
/// TidalSearchProvider, which uses the public catalogue.
/// </summary>
public class TidalClient
{
    /// <summary>How a token check ended.</summary>
    public enum TidalTokenStatus
    {
        /// <summary>The API answered the request; the token works.</summary>
        Valid,

        /// <summary>Tidal refused the token itself (401 or 403).</summary>
        Rejected,

        /// <summary>
        /// The check could not run: the API answered with a server error
        /// or did not answer. The token may well be fine, so the UI must
        /// not call it stale.
        /// </summary>
        Unreachable,
    }

    private readonly HttpClient _http;
    private readonly ILogger<TidalClient> _logger;

    public TidalClient(HttpClient http, ILogger<TidalClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// A cheap token check for the availability verdict: one real search
    /// request. A rejected token is told apart from a check that could
    /// not run, so the UI never sends someone hunting for a new token
    /// over a server hiccup.
    /// </summary>
    public async Task<TidalTokenStatus> ValidateTokenAsync(
        string token, string countryCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return TidalTokenStatus.Rejected;

        var url = "https://api.tidal.com/v1/search?countryCode="
            + Uri.EscapeDataString(string.IsNullOrWhiteSpace(countryCode) ? "US" : countryCode)
            + "&types=TRACKS&limit=1&query=a";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogDebug("Tidal rejected the token ({Status})", (int)response.StatusCode);
                return TidalTokenStatus.Rejected;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tidal token check answered {Status}", (int)response.StatusCode);
                return TidalTokenStatus.Unreachable;
            }
            return TidalTokenStatus.Valid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tidal token check failed");
            return TidalTokenStatus.Unreachable;
        }
    }
}
