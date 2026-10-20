using System.Text;

namespace MelodyBridge.Infrastructure.Services;

// csv export helpers: cell quoting and download filename sanitizing
public static class Csv
{
    /// <summary>wraps a cell in quotes when it holds a comma, quote or newline; doubles embedded quotes.</summary>
    public static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>playlist name as one portable, safe file name.</summary>
    public static string SafeFileName(string name)
        => SafeFileNames.Sanitize(name);
}

internal static class StringTruncateExtensions
{
    public static string? Truncate(this string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? s : (s!.Length <= max ? s : s[..max] + "…");
}
