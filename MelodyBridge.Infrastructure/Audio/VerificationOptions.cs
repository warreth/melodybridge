namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// How strictly a downloaded file is verified after download. One value
/// that the Server seeds from the database settings at startup and the
/// Settings page replaces on save, so a change applies without a restart.
/// </summary>
/// <param name="Spectrum">Spectral verification mode; Off skips the check.</param>
/// <param name="LengthToleranceSeconds">
/// Absolute part of the allowed duration delta: a file may differ from the
/// expected length by at least this many seconds.
/// </param>
/// <param name="LengthTolerancePercent">
/// Relative part of the allowed duration delta, in percent of the expected
/// length. The larger of the two wins, so a long track gets a wider window.
/// </param>
/// <param name="SpectrumClassGap">
/// How many bitrate classes below the declared bitrate the measured
/// bandwidth may sit before the file counts as inflated. 1 is the
/// strictest, 2 the default, 3 the most forgiving.
/// </param>
public record VerificationOptions(
    SpectrumMode Spectrum = SpectrumMode.Fast,
    double LengthToleranceSeconds = 30,
    double LengthTolerancePercent = 10,
    int SpectrumClassGap = 2)
{
    /// <summary>The allowed duration delta for one expected length.</summary>
    public TimeSpan ToleranceFor(TimeSpan expected)
        => TimeSpan.FromSeconds(Math.Max(
            Math.Max(0, LengthToleranceSeconds),
            Math.Max(0, LengthTolerancePercent) / 100.0 * expected.TotalSeconds));

    /// <summary>
    /// Builds the options from stored settings, clamping every value so a
    /// typo cannot disable verification or open it to nonsense. Missing or
    /// unparseable values fall back to the defaults.
    /// </summary>
    public static VerificationOptions FromSettings(
        string? spectrumMode, string? classGap, string? toleranceSeconds, string? tolerancePercent)
    {
        var spectrum = spectrumMode?.Trim().ToLowerInvariant() switch
        {
            "off" => SpectrumMode.Off,
            "thorough" => SpectrumMode.Thorough,
            _ => SpectrumMode.Fast,
        };
        return new(
            spectrum,
            Parse(toleranceSeconds, 30, 0, 600),
            Parse(tolerancePercent, 10, 0, 100),
            (int)Math.Round(Parse(classGap, 2, 1, 4)));
    }

    private static double Parse(string? raw, double fallback, double min, double max)
        => double.TryParse(raw, System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}
