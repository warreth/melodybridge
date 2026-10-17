using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// Reads, and when the plugin settings carry one seeds, the Tidal login
/// tiddl keeps in its app folder (<c>$TIDDL_PATH</c> or
/// <c>~/.tiddl/auth.json</c>). MelodyBridge never owns the Tidal account,
/// exactly like qobuz-dlp and deemix own theirs: the user runs
/// <c>tiddl auth login</c> once or pastes a token (a browser JWT or a
/// full auth.json) into the plugin settings, and both the search and the
/// downloads use that session.
/// </summary>
public static class TidalAuth
{
    /// <summary>
    /// The fields tiddl stores. Only the ones with a value are written;
    /// reading the file fills what it has.
    /// </summary>
    public record AuthData(
        string? Token = null,
        string? RefreshToken = null,
        long ExpiresAt = 0,
        string? UserId = null,
        string? CountryCode = null);

    /// <summary>
    /// The refresh token written when the pasted token has none: tiddl's
    /// CLI refuses to run without one, and a token without a refresh
    /// token works until it expires anyway. The next refresh attempt
    /// fails with the provider's answer, never silently.
    /// </summary>
    public const string NoRefreshPlaceholder = "melodybridge-no-refresh-token";

    /// <summary>The file the settings-derived session is written to, next to tiddl's own.</summary>
    private const string AuthFileName = "auth.json";

    /// <summary>The marker holding the hash of the last seeded session (no secrets).</summary>
    private const string SeedMarkerName = ".melodybridge-token-seed";

    /// <summary>One stored tiddl session.</summary>
    public record Session(
        string Token, long ExpiresAtUnix, string? CountryCode,
        string? UserId = null, bool HasRefreshToken = false)
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
        var data = ReadAuthData(appPath);
        if (data?.Token is null) return null;

        return new Session(data.Token, data.ExpiresAt, data.CountryCode,
            data.UserId, data.RefreshToken is { Length: > 0 });
    }

    /// <summary>The raw stored fields, or null when the file is missing or unreadable.</summary>
    private static AuthData? ReadAuthData(string appPath)
    {
        var path = Path.Combine(appPath, AuthFileName);
        try
        {
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var token = ReadString(root, "token");
            if (string.IsNullOrWhiteSpace(token)) return null;

            return new AuthData(
                token,
                ReadString(root, "refresh_token"),
                ReadLong(root, "expires_at"),
                ReadString(root, "user_id"),
                ReadString(root, "country_code"));
        }
        catch (Exception)
        {
            // An unreadable or half-written auth file is "not logged in",
            // never a crash inside the waterfall.
            return null;
        }
    }

    /// <summary>
    /// Turns the pasted settings into tiddl's auth data: the token field
    /// holds a full auth.json object or a raw access token, the advanced
    /// fields win over what the token itself says, and a JWT contributes
    /// the user id, country and expiry when the settings leave them open.
    /// Null when the value cannot be read at all.
    /// </summary>
    public static AuthData? ParseSettings(
        string tokenField, string refreshField, string userIdField, string countryField)
    {
        var token = tokenField.Trim();
        var refresh = refreshField.Trim();
        var userId = userIdField.Trim();
        var country = countryField.Trim();

        long expiresAt = 0;
        if (token.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(token);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                var root = doc.RootElement;

                if (refresh.Length == 0) refresh = ReadString(root, "refresh_token", "refreshToken", "refresh") ?? "";
                if (userId.Length == 0) userId = ReadString(root, "user_id", "userId", "uid", "userid") ?? "";
                if (country.Length == 0) country = ReadString(root, "country_code", "countryCode", "country") ?? "";
                expiresAt = ReadLong(root, "expires_at", "expiresAt", "expires", "exp");
                token = ReadString(root, "token", "access_token", "accessToken") ?? "";
                if (token.Length == 0) return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else
        {
            // Copying a JWT can drag newlines along; the token itself has none.
            token = token.Replace("\r", "").Replace("\n", "");
        }

        if (token.Length == 0) return null;

        if (TryReadJwtClaims(token, out var jwtExpiresAt, out var jwtUserId, out var jwtCountry))
        {
            if (expiresAt == 0) expiresAt = jwtExpiresAt;
            if (userId.Length == 0) userId = jwtUserId ?? "";
            if (country.Length == 0) country = jwtCountry ?? "";
        }

        return new AuthData(
            token,
            refresh.Length > 0 ? refresh : null,
            expiresAt,
            userId.Length > 0 ? userId : null,
            country.Length > 0 ? country.ToUpperInvariant() : null);
    }

    /// <summary>
    /// Writes the session into tiddl's auth file when the settings differ
    /// from what was seeded last time. tiddl refreshes the file itself, so
    /// the marker (a hash, never the token) keeps a later run from
    /// overwriting a session tiddl just renewed. Returns true when the
    /// file was written. Internal for tests.
    /// </summary>
    internal static bool Seed(string appPath, AuthData data)
    {
        if (data.Token is null) return false;

        Directory.CreateDirectory(appPath);
        var authPath = Path.Combine(appPath, AuthFileName);
        var markerPath = Path.Combine(appPath, SeedMarkerName);

        // Values the settings leave open keep what the file already has,
        // so pasting a fresh token never throws away a real refresh token
        // or the user id and country of an earlier tiddl login.
        var existing = ReadAuthData(appPath);
        var merged = new AuthData(
            data.Token,
            data.RefreshToken ?? existing?.RefreshToken ?? NoRefreshPlaceholder,
            data.ExpiresAt,
            data.UserId ?? existing?.UserId,
            data.CountryCode ?? existing?.CountryCode);

        var fingerprint = Fingerprint(merged);
        try
        {
            if (File.Exists(authPath) && File.Exists(markerPath)
                && File.ReadAllText(markerPath).Trim() == fingerprint)
                return false;
        }
        catch (Exception)
        {
            // An unreadable marker just means seeding again.
        }

        var payload = new Dictionary<string, object?>
        {
            ["token"] = merged.Token,
            ["refresh_token"] = merged.RefreshToken,
            ["expires_at"] = merged.ExpiresAt,
        };
        if (merged.UserId is not null) payload["user_id"] = merged.UserId;
        if (merged.CountryCode is not null) payload["country_code"] = merged.CountryCode;

        File.WriteAllText(authPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(markerPath, fingerprint);
        return true;
    }

    /// <summary>Reads the claims a Tidal JWT carries; false when it is not a JWT.</summary>
    internal static bool TryReadJwtClaims(
        string token, out long expiresAt, out string? userId, out string? countryCode)
    {
        expiresAt = 0;
        userId = null;
        countryCode = null;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            expiresAt = ReadLong(doc.RootElement, "exp");
            userId = ReadString(doc.RootElement,
                "userId", "user_id", "uid", "userid", "sub");
            countryCode = ReadString(doc.RootElement,
                "countryCode", "country_code", "country");
            return true;
        }
        catch (Exception)
        {
            // Not a JWT is not an error: the advanced fields then have to
            // carry the pieces, or the card says what is missing.
            return false;
        }
    }

    /// <summary>A hash over the session values; the same settings always produce the same hash.</summary>
    private static string Fingerprint(AuthData data)
    {
        var text = string.Join('\n',
            data.Token, data.RefreshToken ?? "", data.ExpiresAt.ToString(),
            data.UserId ?? "", data.CountryCode ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>A string field, or null. Numbers are accepted (user ids arrive both ways).</summary>
    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number.ToString();
        }
        return null;
    }

    /// <summary>A number field (a numeric string is accepted), or 0.</summary>
    private static long ReadLong(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), out var parsed))
                return parsed;
        }
        return 0;
    }
}
