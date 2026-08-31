namespace MelodyBridge.Core;

/// <summary>
/// Solves Cloudflare-style challenges for a protected host by returning
/// the headers a plain HTTP client needs to get through (typically a
/// cf_clearance cookie plus the matching User-Agent).
/// </summary>
public interface IChallengeSolver
{
    /// <summary>True when the solver is configured and operational.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Solves the challenge for the given URL. Returns the cookie header
    /// value and User-Agent to use, or null when the solver could not.
    /// </summary>
    Task<CloudflareCredentials?> SolveAsync(string url, CancellationToken ct = default);
}

/// <summary>The headers that get a plain client past the challenge.</summary>
public record CloudflareCredentials(string CookieHeader, string UserAgent);
