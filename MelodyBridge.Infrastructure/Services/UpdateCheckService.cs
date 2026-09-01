using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>The outcome of one update check, ready to render.</summary>
public sealed record UpdateCheckResult(
    bool Succeeded,
    string? LatestVersion,
    string? ReleaseUrl,
    string? Error);

/// <summary>
/// Checks GitHub for a newer release than the running <see cref="AppInfo.Version"/>.
/// Runs only when the user asks (About tab); no telemetry, no background calls.
/// </summary>
public sealed class UpdateCheckService(HttpClient http)
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            // GitHub's API requires a User-Agent; identify ourselves plainly.
            using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.ReleasesFeed);
            request.Headers.UserAgent.ParseAdd($"MelodyBridge/{AppInfo.Version}");
            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, null, null,
                    $"GitHub answered {(int)response.StatusCode}.");

            // Manual parse: the payload is two fields and this keeps
            // deserialization independent of type accessibility rules.
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var latest = doc.RootElement.TryGetProperty("tag_name", out var tag)
                && tag.ValueKind == JsonValueKind.String
                    ? tag.GetString()?.TrimStart('v')
                    : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var link)
                && link.ValueKind == JsonValueKind.String
                    ? link.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateCheckResult(false, null, null, "No release found.");

            return new UpdateCheckResult(true, latest!, url, null);
        }
        catch (Exception ex)
        {
            // Offline or DNS failure is a normal outcome, not an error state.
            return new UpdateCheckResult(false, null, null, ex.Message);
        }
    }

    /// <summary>True when latest differs from the running version (not just newer: any mismatch).</summary>
    public static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var newVersion) &&
            Version.TryParse(current, out var currentVersion))
            return newVersion > currentVersion;

        // Unparsable tags fall back to plain inequality so users still see it.
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }
}
