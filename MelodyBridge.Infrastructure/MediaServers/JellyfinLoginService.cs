using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>Outcome of one Jellyfin sign-in.</summary>
public sealed record JellyfinLoginResult(
    bool Succeeded, string? Token, string? UserId, string? UserName, string? Error)
{
    public static JellyfinLoginResult Fail(string error) => new(false, null, null, null, error);
}

/// <summary>One started Quick Connect request: the secret to poll and the code to show.</summary>
public sealed record JellyfinQuickConnectStart(
    bool Succeeded, string? Secret, string? Code, string? Error)
{
    public static JellyfinQuickConnectStart Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Signs in to Jellyfin, with a username and password or with Quick Connect
/// (the user types a code into Jellyfin and the app polls until it is
/// approved). Playlists in Jellyfin belong to a user: an API key has no
/// user context, so creates and updates fail with it. The wizard signs in
/// here and stores the returned token as the connection credential.
/// </summary>
public class JellyfinLoginService(
    HttpClient http,
    ILogger<JellyfinLoginService> logger)
{
    /// <summary>The modern header: current Jellyfin ignores the legacy
    /// X-Emby-Authorization header and answers 400 without a client name.</summary>
    private const string ClientHeader =
        "MediaBrowser Client=\"MelodyBridge\", Device=\"MelodyBridge\", DeviceId=\"melodybridge\", Version=\"1.0\"";

    private const string QuickConnectOff =
        "Quick Connect is turned off on this server. Enable it in Jellyfin under Dashboard, General.";

    public virtual async Task<JellyfinLoginResult> LoginAsync(
        string baseUrl, string username, string password, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return JellyfinLoginResult.Fail("Enter the server URL first.");
            if (string.IsNullOrWhiteSpace(username))
                return JellyfinLoginResult.Fail("Enter the Jellyfin username.");

            var url = NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/Users/AuthenticateByName";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { Username = username, Pw = password }),
            };
            request.Headers.TryAddWithoutValidation("Authorization", ClientHeader);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return JellyfinLoginResult.Fail(response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Jellyfin rejected the username or password."
                    : $"Jellyfin answered {(int)response.StatusCode}.");
            }

            var result = ParseAuthResult(body);
            if (result.Succeeded)
                logger.LogInformation("Signed in to Jellyfin as {User}", result.UserName ?? username);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Jellyfin sign-in failed");
            return JellyfinLoginResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Starts a Quick Connect request: the returned code goes to the user
    /// (who approves it in Jellyfin) and the secret polls the state.
    /// </summary>
    public virtual async Task<JellyfinQuickConnectStart> StartQuickConnectAsync(
        string baseUrl, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return JellyfinQuickConnectStart.Fail("Enter the server URL first.");
            var root = NormalizeBaseUrl(baseUrl).TrimEnd('/');

            // Quick Connect is a server setting; check it first so the user
            // gets a useful message instead of a bare 401.
            using (var enabled = await SendAsync(HttpMethod.Get, root + "/QuickConnect/Enabled", null, ct))
            {
                if (enabled.IsSuccessStatusCode)
                {
                    var enabledBody = await enabled.Content.ReadAsStringAsync(ct);
                    if (enabledBody.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                        return JellyfinQuickConnectStart.Fail(QuickConnectOff);
                }
            }

            using var response = await SendAsync(HttpMethod.Post, root + "/QuickConnect/Initiate", null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return JellyfinQuickConnectStart.Fail(response.StatusCode == HttpStatusCode.Unauthorized
                    ? QuickConnectOff
                    : $"Jellyfin answered {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(body);
            var secret = doc.RootElement.TryGetProperty("Secret", out var secretElement)
                ? secretElement.GetString() : null;
            var code = doc.RootElement.TryGetProperty("Code", out var codeElement)
                ? codeElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
                return JellyfinQuickConnectStart.Fail("Jellyfin answered without a Quick Connect code.");

            logger.LogInformation("Quick Connect started; waiting for the code to be approved");
            return new JellyfinQuickConnectStart(true, secret, code, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Quick Connect start failed");
            return JellyfinQuickConnectStart.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Polls one Quick Connect request: null while Jellyfin waits for the
    /// user, a result once it is approved (the token exchange) or a failure.
    /// </summary>
    public virtual async Task<JellyfinLoginResult?> FinishQuickConnectAsync(
        string baseUrl, string secret, CancellationToken ct = default)
    {
        try
        {
            var root = NormalizeBaseUrl(baseUrl).TrimEnd('/');
            using (var state = await SendAsync(HttpMethod.Get,
                root + "/QuickConnect/Connect?secret=" + Uri.EscapeDataString(secret), null, ct))
            {
                if (state.StatusCode == HttpStatusCode.NotFound)
                    return JellyfinLoginResult.Fail("The code expired before it was approved. Try again.");
                if (!state.IsSuccessStatusCode)
                    return JellyfinLoginResult.Fail($"Jellyfin answered {(int)state.StatusCode}.");

                var body = await state.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("Authenticated", out var authenticated)
                    || authenticated.ValueKind != JsonValueKind.True)
                {
                    return null; // still waiting for the user
                }
            }

            using var response = await SendAsync(HttpMethod.Post,
                root + "/Users/AuthenticateWithQuickConnect",
                JsonContent.Create(new { Secret = secret }), ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return JellyfinLoginResult.Fail(
                    $"Jellyfin answered {(int)response.StatusCode} on the token exchange.");

            var result = ParseAuthResult(responseBody);
            if (result.Succeeded)
                logger.LogInformation("Quick Connect signed in as {User}", result.UserName);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Quick Connect poll failed");
            return JellyfinLoginResult.Fail(ex.Message);
        }
    }

    /// <summary>Sends one request with the client header attached.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", ClientHeader);
        return await http.SendAsync(request, ct);
    }

    /// <summary>Maps an AuthenticationResult body onto the login result.</summary>
    private static JellyfinLoginResult ParseAuthResult(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.TryGetProperty("AccessToken", out var tokenElement)
            ? tokenElement.GetString() : null;
        var user = root.TryGetProperty("User", out var userElement) ? userElement : default;
        var userId = user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("Id", out var idElement) ? idElement.GetString() : null;
        var userName = user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(token))
            return JellyfinLoginResult.Fail("Jellyfin answered without a session token.");
        return new JellyfinLoginResult(true, token, userId, userName, null);
    }

    /// <summary>Jellyfin URLs need a scheme; default to plain http.</summary>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }
}
