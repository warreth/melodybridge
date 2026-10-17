using System.Text.Json;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// Reads the Tidal login tiddl keeps in its app folder
/// (<c>$TIDDL_PATH</c> or <c>~/.tiddl/auth.json</c>). MelodyBridge never
/// owns the login, exactly like qobuz-dlp and deemix own theirs: the user
/// runs <c>tiddl auth login</c> once and both the search and the downloads
/// use that session.
/// </summary>
public static class TidalAuth
{
    /// <summary>One stored tiddl session.</summary>
    public record Session(string Token, long ExpiresAtUnix, string? CountryCode)
    {
        /// <summary>True when the access token is past its expiry (tiddl refreshes on its next run).</summary>
        public bool Expired => ExpiresAtUnix > 0
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAtUnix;
    }

    /// <summary>
    /// The tiddl app folder: the plugin setting wins, then the environment
    /// variable tiddl itself honours, then the default home folder.
    /// </summary>
    public static string ResolveAppPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var fromEnvironment = Environment.GetEnvironmentVariable("TIDDL_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tiddl");
    }

    /// <summary>The stored session, or null when tiddl was never logged in.</summary>
    public static Session? Read(string appPath)
    {
        var path = Path.Combine(appPath, "auth.json");
        try
        {
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var token = root.TryGetProperty("token", out var tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(token)) return null;

            var expiresAt = root.TryGetProperty("expires_at", out var expiryElement)
                && expiryElement.ValueKind == JsonValueKind.Number
                && expiryElement.TryGetInt64(out var expiry)
                ? expiry
                : 0;

            var country = root.TryGetProperty("country_code", out var countryElement)
                && countryElement.ValueKind == JsonValueKind.String
                ? countryElement.GetString()
                : null;

            return new Session(token, expiresAt, country);
        }
        catch (Exception)
        {
            // An unreadable or half-written auth file is "not logged in",
            // never a crash inside the waterfall.
            return null;
        }
    }
}
