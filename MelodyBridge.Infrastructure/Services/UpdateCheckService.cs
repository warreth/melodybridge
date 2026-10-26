using System.Globalization;
using System.Net;
using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>The outcome of one update check, ready to render.</summary>
public sealed record UpdateCheckResult(
    bool Succeeded,
    string? LatestVersion,
    string? ReleaseUrl,
    string? Error,
    string? ReleaseName = null,
    string? ReleaseBody = null);

/// <summary>
/// Checks GitHub for releases and the running <see cref="AppInfo.Version"/>.
/// A GET to the public GitHub API sends no account data; the app checks on
/// demand and once a day while automatic update checks stay enabled.
/// </summary>
public sealed class UpdateCheckService(HttpClient http)
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var (latest, notFound) = await FetchReleaseAsync(AppInfo.ReleasesFeed, ct);
            if (!notFound) return latest;

            // GitHub answers 404 for /releases/latest while every release
            // is a prerelease. Fall back to the newest published release
            // so beta users still see the release they can upgrade to; a
            // repo without any release keeps the honest error.
            return await FetchNewestReleaseAsync(ct) ?? latest;
        }
        catch (Exception ex)
        {
            // Offline or DNS failure is a normal outcome, not an error state.
            return new UpdateCheckResult(false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// The published release for one exact version, used for the what's-new
    /// dialog after an upgrade. The tag may carry a leading v. Returns null
    /// when that version has no release or GitHub cannot be reached.
    /// </summary>
    public async Task<UpdateCheckResult?> FindReleaseAsync(
        string version, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.ReleasesListFeed);
            request.Headers.UserAgent.ParseAdd($"MelodyBridge/{AppInfo.Version}");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft)
                    && draft.ValueKind == JsonValueKind.True) continue;
                if (!release.TryGetProperty("tag_name", out var tag)
                    || tag.ValueKind != JsonValueKind.String) continue;
                var label = tag.GetString()?.TrimStart('v', 'V');
                if (!string.Equals(label, version, StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = ParseRelease(release);
                if (parsed.Succeeded) return parsed;
            }
            return null;
        }
        catch (Exception)
        {
            // Offline is a normal outcome: the dialog falls back to the
            // release page instead of blocking startup.
            return null;
        }
    }

    /// <summary>One release JSON, or the GitHub status when it failed.</summary>
    private async Task<(UpdateCheckResult Result, bool NotFound)> FetchReleaseAsync(
        string url, CancellationToken ct)
    {
        // GitHub's API requires a User-Agent; identify ourselves plainly.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"MelodyBridge/{AppInfo.Version}");
        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return (new UpdateCheckResult(false, null, null,
                $"GitHub answered {(int)response.StatusCode}."),
                response.StatusCode == HttpStatusCode.NotFound);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (ParseRelease(doc.RootElement), false);
    }

    /// <summary>The newest published (non-draft) release, or null when there is none.</summary>
    private async Task<UpdateCheckResult?> FetchNewestReleaseAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.ReleasesListFeed);
        request.Headers.UserAgent.ParseAdd($"MelodyBridge/{AppInfo.Version}");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            var parsed = ParseRelease(release);
            if (parsed.Succeeded) return parsed;
        }
        return null;
    }

    /// <summary>Maps one GitHub release object onto the result the UI renders.</summary>
    private static UpdateCheckResult ParseRelease(JsonElement release)
    {
        // Manual parse: the payload is two fields and this keeps
        // deserialization independent of type accessibility rules.
        var latest = release.TryGetProperty("tag_name", out var tag)
            && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()?.TrimStart('v')
                : null;
        var url = release.TryGetProperty("html_url", out var link)
            && link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(latest))
            return new UpdateCheckResult(false, null, null, "No release found.");

        var name = release.TryGetProperty("name", out var title)
            && title.ValueKind == JsonValueKind.String
                ? title.GetString()
                : null;
        var body = release.TryGetProperty("body", out var notes)
            && notes.ValueKind == JsonValueKind.String
                ? notes.GetString()
                : null;
        return new UpdateCheckResult(true, latest!, url, null, name, body);
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
