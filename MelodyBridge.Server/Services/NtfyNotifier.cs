using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Posts notification events to an ntfy topic. The toast toggles and the
/// ntfy categories are independent: every category has its own switch on
/// the Advanced page. ntfy takes the message as the POST body, a title,
/// priority and tags as headers, and an optional bearer token for
/// protected topics. Delivery is best effort: a failing topic never
/// breaks the caller's loop.
/// </summary>
public class NtfyNotifier(
    SettingsStore settings,
    IHttpClientFactory httpFactory,
    ILogger<NtfyNotifier> logger)
{
    /// <summary>Setting key per category: ntfy_downloads, ntfy_jobs, ...</summary>
    public static string SettingKeyFor(string category) => "ntfy_" + category;

    /// <summary>Pushes one event when ntfy is on and the category is enabled.</summary>
    public virtual async Task NotifyAsync(string category, string message, string level, CancellationToken ct = default)
    {
        try
        {
            if (!await settings.GetBoolAsync("ntfy_enabled", false, ct)) return;
            if (!await settings.GetBoolAsync(SettingKeyFor(category), true, ct)) return;
            var url = await settings.GetAsync("ntfy_url", "", ct);
            var token = await settings.GetAsync("ntfy_token", "", ct);
            var error = await SendAsync(url, token, message, level, ct);
            if (error is not null)
                logger.LogWarning("ntfy delivery failed: {Error}", error);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ntfy delivery failed");
        }
    }

    /// <summary>Sends a test message with the given values; returns the error, or null when sent.</summary>
    public virtual Task<string?> TestAsync(string url, string? token, CancellationToken ct = default)
        => SendAsync(url, token, "Test notification from MelodyBridge.", "info", ct);

    private async Task<string?> SendAsync(string? url, string? token, string message, string level, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return "No ntfy URL configured.";
            // Validate here: a malformed URL otherwise surfaces as the raw
            // "request URI must be an absolute URI" HttpClient message.
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                return "That URL is not a valid http(s) address.";
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent(message, System.Text.Encoding.UTF8),
            };
            request.Headers.TryAddWithoutValidation("Title", "MelodyBridge");
            request.Headers.TryAddWithoutValidation("Priority", level switch
            {
                "error" => "urgent",
                "warn" => "high",
                _ => "default",
            });
            request.Headers.TryAddWithoutValidation("Tags", level switch
            {
                "error" => "rotating_light",
                "warn" => "warning",
                _ => "white_check_mark",
            });
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            using var response = await httpFactory.CreateClient("ntfy").SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"ntfy answered {(int)response.StatusCode}.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
