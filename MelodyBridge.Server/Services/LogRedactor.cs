using System.Text.RegularExpressions;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Hides credentials that travel inside log messages. Request URLs carry
/// API tokens and salts as query parameters (Navidrome's t= and s=,
/// Jellyfin's api_key=), and the Logs page shows, copies and exports log
/// text, so a raw URL would hand those secrets out. Only the value is
/// masked; the parameter name stays readable for debugging.
/// </summary>
public static partial class LogRedactor
{
    private static readonly string[] SecretParameters =
    {
        "t", "s", "api_key", "apikey", "api-key", "token", "access_token",
        "password", "passwd", "secret", "client_secret", "sp_dc", "tk",
    };

    [GeneratedRegex(@"[?&](?<name>[A-Za-z0-9_\-]+)=(?<value>[^&\s""'<>]+)", RegexOptions.Compiled)]
    private static partial Regex QueryParameter();

    /// <summary>Exact names plus the common "ends with" shapes
    /// (X-Plex-Token, client_secret, ...).</summary>
    private static bool IsSecretParameter(string name)
        => SecretParameters.Contains(name, StringComparer.OrdinalIgnoreCase)
           || name.EndsWith("token", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("password", StringComparison.OrdinalIgnoreCase)
           || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
           || name.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    /// <summary>The same text with secret-looking parameter values replaced by ***.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return QueryParameter().Replace(text, match =>
            IsSecretParameter(match.Groups["name"].Value)
                ? $"{match.Value[0]}{match.Groups["name"].Value}=***"
                : match.Value);
    }
}
