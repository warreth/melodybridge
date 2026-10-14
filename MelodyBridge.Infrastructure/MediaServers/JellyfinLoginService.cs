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

/// <summary>
/// Signs in to Jellyfin with a username and password and returns the
/// session token. Playlists in Jellyfin belong to a user: an API key has no
/// user context, so creates and updates fail with it. The wizard signs in
/// here and stores the returned token as the connection credential.
/// </summary>
public class JellyfinLoginService(
    HttpClient http,
    ILogger<JellyfinLoginService> logger)
{
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
            // The modern header: current Jellyfin ignores the legacy
            // X-Emby-Authorization header here and answers 400 without a
            // client name.
            request.Headers.TryAddWithoutValidation("Authorization",
                "MediaBrowser Client=\"MelodyBridge\", Device=\"MelodyBridge\", DeviceId=\"melodybridge\", Version=\"1.0\"");

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return JellyfinLoginResult.Fail(response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Jellyfin rejected the username or password."
                    : $"Jellyfin answered {(int)response.StatusCode}.");
            }

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

            logger.LogInformation("Signed in to Jellyfin as {User}", userName ?? username);
            return new JellyfinLoginResult(true, token, userId, userName, null);
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

    /// <summary>Jellyfin URLs need a scheme; default to plain http.</summary>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }
}
