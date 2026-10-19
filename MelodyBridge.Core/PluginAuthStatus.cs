namespace MelodyBridge.Core;

/// <summary>
/// How a plugin's own account or token is doing, separate from whether
/// the service or tool is reachable. The Plugins page uses this to put a
/// red mark on a plugin whose login stopped working, instead of showing
/// the generic "unavailable" state that a dead service also gets.
/// </summary>
public enum PluginAuthState
{
    /// <summary>No verdict yet, or the plugin cannot check its login.</summary>
    Unknown,

    /// <summary>The plugin does not use an account or token.</summary>
    NotRequired,

    /// <summary>The stored login works.</summary>
    Valid,

    /// <summary>No login is configured yet.</summary>
    Missing,

    /// <summary>The stored login is past its expiry.</summary>
    Expired,

    /// <summary>The service refused the stored token.</summary>
    Rejected,

    /// <summary>The account exists, but the service blocks it from downloading.</summary>
    Blocked,

    /// <summary>The login works, but the account's plan does not allow the download.</summary>
    PlanRestricted,

    /// <summary>The login could not be checked (the service did not answer).</summary>
    Unreachable,
}

/// <summary>
/// One plugin's login verdict. <paramref name="Message"/> is the short,
/// human-readable reason shown on the card and in the warning banner;
/// when the state is <see cref="PluginAuthState.Valid"/> it can carry a
/// non-blocking note, such as a plan without lossless.
/// </summary>
public sealed record PluginAuthStatus(PluginAuthState State, string? Message = null)
{
    public static readonly PluginAuthStatus Unknown = new(PluginAuthState.Unknown);
    public static readonly PluginAuthStatus NotRequired = new(PluginAuthState.NotRequired);
    public static readonly PluginAuthStatus Valid = new(PluginAuthState.Valid);

    /// <summary>
    /// True only for a login that used to work and now needs the user:
    /// a dead token, a rejected session, a blocked account or a plan that
    /// cannot download. A missing login or an unreachable service is
    /// reported as unavailable, not as a credential problem.
    /// </summary>
    public bool NeedsAttention => State is
        PluginAuthState.Expired
        or PluginAuthState.Rejected
        or PluginAuthState.Blocked
        or PluginAuthState.PlanRestricted;
}

/// <summary>
/// Implemented by plugins whose availability depends on an account or a
/// token. The Plugins page and the download waterfall read the last
/// verdict from here; plugins without credentials simply do not
/// implement it.
/// </summary>
public interface IPluginAuthStatusProvider
{
    PluginAuthStatus AuthStatus { get; }
}

/// <summary>
/// Implemented by plugins that cache an availability verdict. Saving a
/// token must make the next probe real instead of reusing the old "down"
/// answer for another half minute.
/// </summary>
public interface IPluginAvailabilityRefresh
{
    void RefreshAvailability();
}
