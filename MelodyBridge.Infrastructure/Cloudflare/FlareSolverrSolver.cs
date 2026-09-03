using MelodyBridge.Core;
using System.Net;
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
    /// <summary>Deadline for one auto-detect probe: the candidates are on
    /// the local Docker network, so a slow answer is a wrong answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Minimum spacing between failed auto-detect sweeps.</summary>
    private static readonly TimeSpan NegativeCacheWindow = TimeSpan.FromSeconds(60);

    /// <summary>Auto-detect candidates in probe order: the compose network
    /// name first, then the host loopbacks for non-compose runs. Public so
    /// the Settings test button reports the same sweep the solver runs.</summary>
    public static readonly string[] AutoDetectCandidates =
    {
        "http://flaresolverr:8191",
        "http://host.docker.internal:8191",
        "http://127.0.0.1:8191",
    };

    private readonly HttpClient _http;
    private readonly ILogger<FlareSolverrSolver> _logger;

    // Auto-detection state shared by every solver instance, guarded by
    // DetectionGate: the resolved base URL plus a negative cache so a
    // missing FlareSolverr is not re-probed on every waterfall hit.
    private static readonly object DetectionGate = new();
    private static string _url = "off";
    private static string? _detectedUrl;
    private static DateTimeOffset _lastFailedSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// FlareSolverr endpoint, e.g. http://flaresolverr:8191. "off" or
    /// empty disables the solver; "auto" (case-insensitive) probes the
    /// Docker network candidates. Settable at runtime by the Settings
    /// page.
    /// </summary>
    public static string Url
    {
        get => _url;
        set
        {
            _url = value;
            // A new setting invalidates anything the old mode detected.
            lock (DetectionGate)
            {
                _detectedUrl = null;
                _lastFailedSweep = DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>True when Url is "auto": the solver should probe for an instance.</summary>
    public static bool IsAutoMode =>
        string.Equals(Url, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Injectable clock so tests can age the detection negative cache.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public FlareSolverrSolver(
        HttpClient http,
        Microsoft.Extensions.Options.IOptions<FlareSolverrOptions> options,
        ILogger<FlareSolverrSolver> logger)
    {
        _http = http;
        _logger = logger;
        Url = options.Value.Url;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        // An explicit URL is trusted without a round-trip; auto mode has
        // to find a live instance first.
        if (!IsAutoMode) return true;
        return await DetectAsync(ct) is not null;
    }

    private static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !Url.Equals("off", StringComparison.OrdinalIgnoreCase);

    public async Task<CloudflareCredentials?> SolveAsync(string url, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        string? baseUrl = IsAutoMode ? await DetectAsync(ct) : Url.TrimEnd('/');
        if (baseUrl is null) return null;

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

    /// <summary>
    /// Resolves auto mode to a concrete base URL: the cached detection
    /// when present, an immediate null inside the negative-cache window
    /// after a failed sweep, or a fresh probe otherwise. Never throws.
    /// </summary>
    private async Task<string?> DetectAsync(CancellationToken ct)
    {
        lock (DetectionGate)
        {
            if (_detectedUrl is not null) return _detectedUrl;
            if (Clock() - _lastFailedSweep < NegativeCacheWindow) return null;
        }

        var detected = await SweepAsync(ct);

        lock (DetectionGate)
        {
            if (detected is null)
                _lastFailedSweep = Clock();
            else
            {
                _detectedUrl = detected;
                _lastFailedSweep = DateTimeOffset.MinValue;
            }
        }

        if (detected is null)
            _logger.LogDebug("FlareSolverr auto-detect found no instance");
        else
            _logger.LogInformation("FlareSolverr detected at {Url}", detected);
        return detected;
    }

    /// <summary>Probes each candidate's /health endpoint; the first HTTP 200 wins.</summary>
    private async Task<string?> SweepAsync(CancellationToken ct)
    {
        foreach (var baseUrl in AutoDetectCandidates)
        {
            try
            {
                // The shared "flaresolverr" client allows minutes for solves,
                // so each probe carries its own short deadline instead.
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(ProbeTimeout);
                using var response = await _http.GetAsync($"{baseUrl}/health", probe.Token);
                if (response.StatusCode == HttpStatusCode.OK) return baseUrl;
            }
            catch (Exception)
            {
                // Unreachable candidate: fall through to the next one.
            }
        }
        return null;
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
    /// <summary>Base URL, e.g. http://flaresolverr:8191. "off" disables,
    /// "auto" probes the Docker network candidates.</summary>
    public string Url { get; set; } = "off";
}
