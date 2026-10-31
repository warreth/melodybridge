using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// The slskd connection shared by the search provider and the downloader:
/// base URL and API key from the plugin config, the JSON request plumbing,
/// the cached downloads folder and the short availability verdict.
/// </summary>
public sealed class SlskdSession
{
    public const string DefaultBaseUrl = "http://127.0.0.1:5030";
    public const string BaseUrlKey = "base_url";
    public const string ApiKeyKey = "api_key";
    public const string DownloadsPathKey = "downloads_path";

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly IPluginConfig _config;
    private readonly ILogger<SlskdSession> _logger;
    private readonly AvailabilityCache _availability = new();

    private string? _detectedDownloadsPath;
    private string? _detectedForBaseUrl;

    public SlskdSession(
        HttpClient http, IPluginConfig config, ILogger<SlskdSession> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public string? LastUnavailableReason { get; private set; }

    public void SetUnavailable(string? reason) => LastUnavailableReason = reason;

    /// <summary>Per-request deadline for the small JSON calls.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public async Task<string> BaseUrlAsync(CancellationToken ct)
    {
        var configured = await _config.GetAsync("slskd", BaseUrlKey, ct);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultBaseUrl;
        return configured.Trim().TrimEnd('/');
    }

    public async Task<string> ApiKeyAsync(CancellationToken ct)
        => (await _config.GetAsync("slskd", ApiKeyKey, ct)).Trim();

    /// <summary>The folder slskd writes finished downloads to.</summary>
    public async Task<string?> DownloadsPathAsync(CancellationToken ct)
    {
        var configured = (await _config.GetAsync("slskd", DownloadsPathKey, ct)).Trim();
        if (configured.Length > 0) return configured;

        var baseUrl = await BaseUrlAsync(ct);
        if (_detectedDownloadsPath is not null && _detectedForBaseUrl == baseUrl)
            return _detectedDownloadsPath;
        ForgetDetectedDownloadsPath();

        using var options = await SendJsonAsync(HttpMethod.Get, "/api/v0/options", null, ct);
        if (options is not null &&
            options.RootElement.TryGetProperty("directories", out var directories) &&
            directories.ValueKind == JsonValueKind.Object)
        {
            var downloads = SlskdJson.GetString(directories, "downloads");
            if (!string.IsNullOrEmpty(downloads))
            {
                _detectedDownloadsPath = downloads;
                _detectedForBaseUrl = baseUrl;
                _logger.LogDebug("slskd reports its downloads folder as {Folder}", downloads);
                return downloads;
            }
        }
        return null;
    }

    public void ForgetDetectedDownloadsPath()
    {
        _detectedDownloadsPath = null;
        _detectedForBaseUrl = null;
    }

    internal string? DetectedDownloadsPath => _detectedDownloadsPath;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached)) return cached;

        if ((await ApiKeyAsync(ct)).Length == 0)
        {
            SetUnavailable("Set the slskd API key");
            _logger.LogDebug("slskd: no API key configured");
            _availability.Store(false);
            return false;
        }

        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(HealthTimeout);
            using var application = await SendJsonAsync(
                HttpMethod.Get, "/api/v0/application", null, probe.Token);
            if (application is null)
            {
                SetUnavailable("slskd did not answer (check the URL and API key)");
                _availability.Store(false);
                return false;
            }
            if (!IsServerLoggedIn(application.RootElement))
            {
                SetUnavailable("slskd is not logged in to Soulseek");
                _logger.LogDebug("slskd is reachable but not logged in to the Soulseek network");
                _availability.Store(false);
                return false;
            }
            if (await DownloadsPathAsync(probe.Token) is null)
            {
                SetUnavailable("slskd's downloads folder could not be read");
                _availability.Store(false);
                return false;
            }

            SetUnavailable(null);
            _availability.Store(true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetUnavailable("slskd did not answer (check the URL and API key)");
            _logger.LogDebug(ex, "slskd health probe failed");
            _availability.Store(false);
            return false;
        }
    }

    public void RefreshAvailability() => _availability.Invalidate();

    /// <summary>True when slskd reports a connected, logged-in server.</summary>
    internal static bool IsServerLoggedIn(JsonElement root)
    {
        if (!root.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object)
            return false;
        var connected = server.TryGetProperty("isConnected", out var c) && c.ValueKind == JsonValueKind.True;
        var loggedIn = server.TryGetProperty("isLoggedIn", out var l) && l.ValueKind == JsonValueKind.True;
        return connected && loggedIn;
    }

    public async Task<JsonDocument?> SendJsonAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var apiKey = await ApiKeyAsync(ct);
        if (apiKey.Length == 0) return null;

        var baseUrl = await BaseUrlAsync(ct);
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Add("X-API-Key", apiKey);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("slskd {Method} {Path} returned {Status}",
                method, path, (int)response.StatusCode);
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            _logger.LogDebug("slskd {Path} returned a non-JSON body", path);
            return null;
        }
    }
}

/// <summary>Small JSON readers shared by the slskd session and its search provider.</summary>
internal static class SlskdJson
{
    public static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
