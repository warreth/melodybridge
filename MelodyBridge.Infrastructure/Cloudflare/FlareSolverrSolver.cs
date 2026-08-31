using MelodyBridge.Core;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Cloudflare;

/// <summary>
/// Solves Cloudflare challenges through a FlareSolverr instance
/// (https://github.com/FlareSolverr/FlareSolverr), typically run as a
/// Docker service. The solver opens a real browser, waits out the
/// challenge, and returns the cf_clearance cookie with the matching
/// User-Agent.
/// </summary>
public class FlareSolverrSolver : IChallengeSolver
{
    private readonly HttpClient _http;
    private readonly ILogger<FlareSolverrSolver> _logger;

    /// <summary>
    /// FlareSolverr endpoint, e.g. http://flaresolverr:8191.
    /// "Off" or empty disables the solver. Settable at runtime by the
    /// Settings page.
    /// </summary>
    public static string Url { get; set; } = "off";

    public FlareSolverrSolver(
        HttpClient http,
        Microsoft.Extensions.Options.IOptions<FlareSolverrOptions> options,
        ILogger<FlareSolverrSolver> logger)
    {
        _http = http;
        _logger = logger;
        Url = options.Value.Url;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(IsConfigured);

    private static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !Url.Equals("off", StringComparison.OrdinalIgnoreCase);

    public async Task<CloudflareCredentials?> SolveAsync(string url, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        var baseUrl = Url.TrimEnd('/');

        var request = new
        {
            cmd = "request.get",
            url,
            // A session keeps the solved browser alive for follow-ups.
            session = "melodybridge",
            maxTimeout = 60000,
        };

        try
        {
            var response = await _http.PostAsJsonAsync($"{baseUrl}/v1", request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(ct);
            if (body?.Status != "ok" || body.Solution is null)
            {
                _logger.LogWarning("FlareSolverr could not solve {Url}: {Message}",
                    url, body?.Message ?? "unknown error");
                return null;
            }

            var cookie = string.Join("; ",
                (body.Solution.Cookies ?? Array.Empty<FlareSolverrCookie>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => $"{c.Name}={c.Value}"));
            var userAgent = body.Solution.UserAgent;

            if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(userAgent))
            {
                _logger.LogWarning("FlareSolverr returned no clearance for {Url}", url);
                return null;
            }

            return new CloudflareCredentials(cookie, userAgent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("FlareSolverr request failed for {Url}: {Message}",
                url, ex.Message);
            return null;
        }
    }

    // FlareSolverr response shape (only the fields we read).
    private record FlareSolverrResponse(
        string Status, string? Message, FlareSolverrSolution? Solution);
    private record FlareSolverrSolution(
        string Url, string Status, FlareSolverrCookie[]? Cookies, string UserAgent);
    private record FlareSolverrCookie(string Name, string Value, string? Domain);
}

/// <summary>FlareSolverr connection settings (appsettings "FlareSolverr" section).</summary>
public class FlareSolverrOptions
{
    /// <summary>Base URL, e.g. http://flaresolverr:8191. "off" disables.</summary>
    public string Url { get; set; } = "off";
}
