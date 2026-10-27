using MelodyBridge.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
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
///
/// Every solve is expensive (a browser start and a challenge), so the
/// clearance is cached per target host and only refreshed when the site
/// answers a 403 or the cache ages out. Auto mode probes the Docker
/// network candidates once and remembers the first healthy one.
/// </summary>
public class FlareSolverrSolver : IChallengeSolver
{
    /// <summary>Deadline for one auto-detect probe: the candidates are on
    /// the local Docker network, so a slow answer is a wrong answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a healthy endpoint answer is trusted.</summary>
    private static readonly TimeSpan HealthCacheWindow = TimeSpan.FromSeconds(45);

    /// <summary>Minimum spacing between failed auto-detect sweeps. Short
    /// enough that a solver which starts later is picked up by the next
    /// track instead of requiring a manual test.</summary>
    private static readonly TimeSpan NegativeCacheWindow = TimeSpan.FromSeconds(15);

    /// <summary>How long a solved clearance is reused. The site still
    /// invalidates it earlier on a 403, which the plugin handles.</summary>
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Auto-detect candidates in probe order: the two compose
    /// container identities (the service name and the shipped container
    /// name) first, then the host loopbacks for non-compose runs. Public
    /// so the Settings test button reports the same sweep the solver
    /// runs.</summary>
    public static readonly string[] AutoDetectCandidates =
    {
        "http://flaresolverr:8191",
        "http://mb_flaresolverr:8191",
        "http://host.docker.internal:8191",
        "http://127.0.0.1:8191",
    };

    private sealed record CachedCredentials(CloudflareCredentials Value, DateTimeOffset ExpiresAt);

    private readonly HttpClient _http;
    private readonly ILogger<FlareSolverrSolver> _logger;

    // Auto-detection state shared by every solver instance, guarded by
    // DetectionGate: the resolved base URL plus a negative cache so a
    // missing FlareSolverr is not re-probed on every waterfall hit.
    private static readonly object DetectionGate = new();
    private static string _url = "off";
    private static string? _detectedUrl;
    private static DateTimeOffset _lastFailedSweep = DateTimeOffset.MinValue;

    // Cleared credentials, health answers and one solve gate per target
    // host. The gate keeps two parallel downloads from opening two browser
    // challenges for the same site at the same time.
    private static readonly ConcurrentDictionary<string, CachedCredentials> Credentials =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (bool Ok, DateTimeOffset ExpiresAt)> Health =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SolveGates =
        new(StringComparer.OrdinalIgnoreCase);

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
            // A new setting invalidates anything the old mode detected or
            // solved against the old endpoint.
            lock (DetectionGate)
            {
                _detectedUrl = null;
                _lastFailedSweep = DateTimeOffset.MinValue;
            }
            Credentials.Clear();
            Health.Clear();
        }
    }

    /// <summary>True when Url is "auto": the solver should probe for an instance.</summary>
    public static bool IsAutoMode =>
        string.Equals(Url, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Injectable clock so tests can age the caches.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Test hook: clears every static cache without changing the URL.</summary>
    internal static void ResetCaches()
    {
        Credentials.Clear();
        Health.Clear();
        lock (DetectionGate)
        {
            _detectedUrl = null;
            _lastFailedSweep = DateTimeOffset.MinValue;
        }
    }

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

        // Both modes need a live endpoint. An explicit URL used to be
        // trusted blindly, which made a wrong URL look ready until the
        // first track failed.
        var baseUrl = IsAutoMode ? await DetectAsync(ct) : NormalizeEndpoint(Url);
        return baseUrl is not null && await IsHealthyAsync(baseUrl, ct);
    }

    private static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !Url.Equals("off", StringComparison.OrdinalIgnoreCase);

    public Task<CloudflareCredentials?> SolveAsync(string url, CancellationToken ct = default)
        => SolveCoreAsync(url, forceRefresh: false, ct);

    public Task<CloudflareCredentials?> SolveFreshAsync(string url, CancellationToken ct = default)
        => SolveCoreAsync(url, forceRefresh: true, ct);

    private async Task<CloudflareCredentials?> SolveCoreAsync(
        string url, bool forceRefresh, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var host = HostKey(url);
        if (!forceRefresh && TryGetCached(host, out var cached)) return cached;

        var gate = SolveGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have solved while this one waited.
            if (!forceRefresh && TryGetCached(host, out cached)) return cached;

            Credentials.TryRemove(host, out _);
            var baseUrl = IsAutoMode ? await DetectAsync(ct) : NormalizeEndpoint(Url);
            if (baseUrl is null) return null;

            var result = await RequestSolveAsync(_http, baseUrl, url, _logger, ct);
            if (result is null)
            {
                // A dead endpoint must be re-detected instead of being
                // remembered for the rest of the process.
                lock (DetectionGate) _detectedUrl = null;
                return null;
            }

            Credentials[host] = new CachedCredentials(
                result, Clock() + CredentialLifetime);
            _logger.LogInformation("FlareSolverr solved {Host} via {Endpoint}", host, baseUrl);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetCached(string host, out CloudflareCredentials credentials)
    {
        if (Credentials.TryGetValue(host, out var cached) && Clock() < cached.ExpiresAt)
        {
            credentials = cached.Value;
            return true;
        }

        Credentials.TryRemove(host, out _);
        credentials = null!;
        return false;
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

        var detected = await SweepAsync(_http, ct);

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
    private static async Task<string?> SweepAsync(
        HttpClient http, CancellationToken ct, bool force = false)
    {
        foreach (var baseUrl in AutoDetectCandidates)
        {
            if (await IsHealthyAsync(http, baseUrl, ct, force)) return baseUrl;
        }
        return null;
    }

    private async Task<bool> IsHealthyAsync(string baseUrl, CancellationToken ct)
        => await IsHealthyAsync(_http, baseUrl, ct);

    private static async Task<bool> IsHealthyAsync(
        HttpClient http, string baseUrl, CancellationToken ct, bool force = false)
    {
        if (!force && Health.TryGetValue(baseUrl, out var cached) && Clock() < cached.ExpiresAt)
            return cached.Ok;

        var ok = false;
        try
        {
            // The shared "flaresolverr" client allows minutes for solves,
            // so each probe carries its own short deadline instead.
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(ProbeTimeout);
            using var response = await http.GetAsync($"{baseUrl}/health", probe.Token);
            ok = response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception)
        {
            // Unreachable candidate: cache the negative answer briefly.
        }

        Health[baseUrl] = (ok, Clock() + HealthCacheWindow);
        return ok;
    }

    /// <summary>
    /// One request.get through FlareSolverr. The response carries every
    /// cookie and the exact User-Agent the browser used; both are needed
    /// for the follow-up requests. No session is created: concurrent
    /// solves then cannot queue on or invalidate each other's browser.
    /// </summary>
    private static async Task<CloudflareCredentials?> RequestSolveAsync(
        HttpClient http, string baseUrl, string url,
        ILogger<FlareSolverrSolver> logger, CancellationToken ct)
    {
        var request = new
        {
            cmd = "request.get",
            url,
            maxTimeout = 60000,
        };

        try
        {
            using var response = await http.PostAsJsonAsync($"{baseUrl}/v1", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("FlareSolverr answered HTTP {Status} for {Url}: {Body}",
                    (int)response.StatusCode, url, Truncate(errorBody, 300));
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(ct);
            if (body?.Status != "ok" || body.Solution is null)
            {
                logger.LogWarning("FlareSolverr could not solve {Url}: {Message}",
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
                logger.LogWarning("FlareSolverr returned no clearance for {Url}", url);
                return null;
            }

            return new CloudflareCredentials(cookie, userAgent);
        }
        catch (Exception ex)
        {
            logger.LogWarning("FlareSolverr request failed for {Url}: {Message}",
                url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Diagnostic probe for the Settings page: resolve the configured
    /// endpoint, then solve lucida.to for real. A /health answer alone
    /// says nothing about whether the browser can finish a challenge.
    /// </summary>
    public static async Task<ChallengeProbeResult> ProbeAsync(
        string configuredUrl, string targetUrl, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        return await ProbeAsync(http, configuredUrl, targetUrl, ct);
    }

    internal static async Task<ChallengeProbeResult> ProbeAsync(
        HttpClient http, string configuredUrl, string targetUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl)
            || configuredUrl.Equals("off", StringComparison.OrdinalIgnoreCase))
            return new ChallengeProbeResult(false, "Solver is off: Lucida stays unavailable.");

        var endpoint = configuredUrl.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? await SweepAsync(http, ct, force: true)
            : NormalizeEndpoint(configuredUrl);

        if (endpoint is null)
        {
            return new ChallengeProbeResult(false,
                configuredUrl.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? "No FlareSolverr answered on the Docker network. Check the container."
                    : "The configured FlareSolverr URL did not answer on /health.");
        }

        var started = Stopwatch.StartNew();
        var credentials = await RequestSolveAsync(
            http, endpoint, targetUrl, Microsoft.Extensions.Logging.Abstractions
                .NullLogger<FlareSolverrSolver>.Instance, ct);
        started.Stop();

        return credentials is null
            ? new ChallengeProbeResult(false,
                $"FlareSolverr answered at {endpoint} but could not solve lucida.to.", endpoint)
            : new ChallengeProbeResult(true,
                $"Solved lucida.to via {endpoint} in {started.Elapsed.TotalSeconds:0.0}s.", endpoint);
    }

    /// <summary>Adds http:// to a bare host and strips a trailing slash.</summary>
    public static string? NormalizeEndpoint(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0 || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private static string HostKey(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : url.Trim().ToLowerInvariant();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    // FlareSolverr response shape (only the fields we read). The
    // solution's status is a JSON number on current releases and a string
    // on older ones, and nothing here reads it: a JsonElement accepts
    // either shape, where a string caused every solve to fail with a
    // deserialization error.
    private record FlareSolverrResponse(
        string Status, string? Message, FlareSolverrSolution? Solution);
    private record FlareSolverrSolution(
        string? Url, System.Text.Json.JsonElement Status,
        FlareSolverrCookie[]? Cookies, string? UserAgent);
    private record FlareSolverrCookie(string Name, string Value, string? Domain);
}

/// <summary>Result of the Settings connection test.</summary>
public sealed record ChallengeProbeResult(bool Ok, string Message, string? Endpoint = null);

/// <summary>FlareSolverr connection settings (appsettings "FlareSolverr" section).</summary>
public class FlareSolverrOptions
{
    /// <summary>Base URL, e.g. http://flaresolverr:8191. "off" disables,
    /// "auto" probes the Docker network candidates.</summary>
    public string Url { get; set; } = "off";
}
