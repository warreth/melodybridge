using System.Globalization;
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

    /// <summary>
    /// True when the latest tag is newer than the running version. Both
    /// sides are compared as SemVer 2.0: numeric identifiers numerically,
    /// a prerelease sorts before its release, build metadata is ignored.
    /// Unparsable tags fall back to plain inequality so a user still sees
    /// something instead of silence.
    /// </summary>
    public static bool IsNewer(string latest, string current)
    {
        var newVersion = ParseSemver(latest);
        var currentVersion = ParseSemver(current);
        if (newVersion is null || currentVersion is null)
            return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
        return Compare(newVersion.Value, currentVersion.Value) > 0;
    }

    /// <summary>Major.minor.patch plus the optional prerelease label; null when unparsable.</summary>
    internal static (int Major, int Minor, int Patch, string? Prerelease)? ParseSemver(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim().TrimStart('v', 'V');

        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
            if (prerelease.Length == 0) return null;
        }

        // Build metadata never participates in precedence.
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3) return null;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return (numbers[0], numbers[1], numbers[2], prerelease);
    }

    private static int Compare(
        (int Major, int Minor, int Patch, string? Prerelease) a,
        (int Major, int Minor, int Patch, string? Prerelease) b)
    {
        var core = a.Major.CompareTo(b.Major);
        if (core != 0) return core;
        core = a.Minor.CompareTo(b.Minor);
        if (core != 0) return core;
        core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;

        // A release outranks any prerelease of the same core version.
        if (a.Prerelease is null && b.Prerelease is null) return 0;
        if (a.Prerelease is null) return 1;
        if (b.Prerelease is null) return -1;
        return ComparePrerelease(a.Prerelease, b.Prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length) return -1;
            if (i >= right.Length) return 1;

            var lNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ln);
            var rNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rn);

            int comparison;
            if (lNumeric && rNumeric) comparison = ln.CompareTo(rn);
            else if (lNumeric) comparison = -1; // numeric identifiers sort below alphanumeric
            else if (rNumeric) comparison = 1;
            else comparison = string.CompareOrdinal(left[i], right[i]);

            if (comparison != 0) return comparison;
        }
        return 0;
    }
}
