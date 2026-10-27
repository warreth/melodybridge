using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Qobuz;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// The Qobuz login both the downloader and the search provider use. The
/// token can come from the plugin setting or from qobuz-dlp's own token
/// file; the session also owns the auth verdict the Plugins card shows, so
/// a rejected token found by a search updates the card exactly like the
/// availability probe does.
/// </summary>
public sealed class QobuzSession
{
    public const string PluginId = "qobuz";
    public const string TokenKey = "user_auth_token";
    private const string ToolLabel = "qobuz-dlp";

    private readonly IPluginConfig _config;
    private readonly ILogger<QobuzSession> _logger;
    private readonly string _homeRoot;

    public QobuzSession(
        IPluginConfig config, ILogger<QobuzSession> logger, string? homeRoot = null)
    {
        _config = config;
        _logger = logger;
        _homeRoot = homeRoot ?? DefaultHomeRoot();
    }

    /// <summary>The login verdict shown on the plugin card.</summary>
    public PluginAuthStatus AuthStatus { get; private set; } = PluginAuthStatus.Unknown;

    /// <summary>Why the source is not ready, when it is not.</summary>
    public string? LastUnavailableReason { get; private set; }

    /// <summary>The root qobuz-dlp resolves its config folder from.</summary>
    internal static string DefaultHomeRoot()
        => OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>qobuz-dlp's config folder as the tool computes it.</summary>
    public string ToolConfigFolder
        => OperatingSystem.IsWindows()
            ? Path.Combine(_homeRoot, "qobuz-dl")
            : Path.Combine(_homeRoot, ".config", "qobuz-dl");

    /// <summary>The env that makes the CLI read exactly that config folder.</summary>
    public IReadOnlyDictionary<string, string> ToolEnvironment()
        => OperatingSystem.IsWindows()
            ? new Dictionary<string, string> { ["APPDATA"] = _homeRoot }
            : new Dictionary<string, string> { ["HOME"] = _homeRoot };

    /// <summary>
    /// The user token: the plugin setting wins, an empty setting falls
    /// back to qobuz-dlp's own token file so a logged-in tool just works.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var configured = (await _config.GetAsync(PluginId, TokenKey, ct)).Trim();
        return configured.Length > 0 ? configured : ReadTokenFile(ToolConfigFolder);
    }

    /// <summary>The token file inside a qobuz-dlp config folder, or null.</summary>
    public static string? ReadTokenFile(string configFolder)
    {
        var path = Path.Combine(configFolder, ".qobuz_dl.oauth.txt");
        try
        {
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// qobuz-dlp's stored OAuth token. The tool's config folder changed
    /// name between builds ("qobuz-dl" in the PyPI wheel, "qobuz-dlp" in
    /// some checkouts), so both are tried.
    /// </summary>
    public static string? ReadQobuzDlpTokenFile()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return ReadTokenFile(Path.Combine(root, "qobuz-dl"))
            ?? ReadTokenFile(Path.Combine(root, "qobuz-dlp"));
    }

    /// <summary>Sets the login verdict and its short reason, logging a change once.</summary>
    public void SetAuth(
        PluginAuthState state,
        string? message = null,
        string? account = null,
        string? plan = null,
        string? tier = null,
        DateTimeOffset? expiresAt = null)
    {
        var previous = AuthStatus.State;
        AuthStatus = new PluginAuthStatus(state, message, account, plan, tier, expiresAt);

        if (previous == state) return;

        if (state is PluginAuthState.Expired
            or PluginAuthState.Rejected
            or PluginAuthState.Blocked
            or PluginAuthState.PlanRestricted)
        {
            _logger.LogWarning("{Tool}: {Reason}", ToolLabel,
                message ?? "the stored login needs attention");
        }
        else if (state == PluginAuthState.Valid
                 && previous is PluginAuthState.Expired
                     or PluginAuthState.Rejected
                     or PluginAuthState.Blocked
                     or PluginAuthState.PlanRestricted)
        {
            _logger.LogInformation("{Tool}: the stored login works again", ToolLabel);
        }
        else
        {
            _logger.LogDebug("{Tool}: login state {State}", ToolLabel, state);
        }
    }

    /// <summary>Marks the login valid without dropping the known account details.</summary>
    public void SetAuthValid(string? message = null) => SetAuth(
        PluginAuthState.Valid, message,
        AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);

    /// <summary>Records why the source is not ready; null clears it.</summary>
    public void SetUnavailable(string? reason) => LastUnavailableReason = reason;

    /// <summary>
    /// Reads a search outcome into the login state: a blocked or refused
    /// token becomes the reason the plugin is skipped, so the card, the nav
    /// mark and the download failure all say what is wrong instead of "no
    /// source had this track".
    /// </summary>
    public bool AcceptSearchResult(QobuzClient.QobuzSearchResult result)
    {
        switch (result.Status)
        {
            case QobuzClient.QobuzSearchStatus.Ok:
                SetAuthValid();
                LastUnavailableReason = null;
                return true;

            case QobuzClient.QobuzSearchStatus.Blocked:
                SetUnavailable(result.Message
                    ?? "Qobuz blocked this account from downloading");
                SetAuth(PluginAuthState.Blocked, LastUnavailableReason,
                    AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);
                _logger.LogDebug("Qobuz: {Reason}", LastUnavailableReason);
                return false;

            case QobuzClient.QobuzSearchStatus.Rejected:
                SetUnavailable(result.Message
                    ?? "The Qobuz token was rejected; paste a fresh one");
                SetAuth(PluginAuthState.Rejected, LastUnavailableReason,
                    AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);
                _logger.LogDebug("Qobuz: {Reason}", LastUnavailableReason);
                return false;

            default:
                SetUnavailable(result.Message
                    ?? "The Qobuz API did not answer");
                SetAuth(PluginAuthState.Unreachable, LastUnavailableReason,
                    AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);
                _logger.LogDebug("Qobuz: {Reason}", LastUnavailableReason);
                return false;
        }
    }
}
