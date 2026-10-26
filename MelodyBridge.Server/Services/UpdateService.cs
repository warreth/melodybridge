using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Server.Services;

/// <summary>One release's what's-new copy, ready for the dialog.</summary>
public sealed record ReleaseNotesInfo(
    string Version, string? Title, string? Body, string? Url);

/// <summary>
/// Whether the database file already existed when the process started.
/// A missing file means first install; an existing one can be an upgrade
/// even when the version marker is absent (the release before this feature).
/// </summary>
public sealed record InstallState(bool DatabaseExistedAtStartup);

/// <summary>
/// Turns the update check into the two user-facing surfaces: the one-time
/// what's-new dialog after an upgrade, and a notification when a newer
/// version exists. The baseline is the version the app first ran with, so
/// a fresh install never gets release notes for itself. Both features obey
/// the automatic update check setting.
/// </summary>
public sealed class UpdateService(
    UpdateCheckService checks,
    SettingsStore settings,
    NotificationService notifications,
    InstallState install,
    ILogger<UpdateService> logger)
{
    public const string EnabledKey = "update_check_enabled";
    public const string SeenVersionKey = "update_seen_version";
    public const string NotifiedVersionKey = "update_notified_version";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _releaseNotesChecked;

    /// <summary>The pending what's-new copy, or null when there is none.</summary>
    public ReleaseNotesInfo? PendingReleaseNotes { get; private set; }

    /// <summary>The latest release from the last successful check.</summary>
    public UpdateCheckResult? Available { get; private set; }

    /// <summary>Raised when pending notes or the available version change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Runs once per process: no dialog on a first install, one dialog for
    /// the running version after an upgrade, with the release body when
    /// GitHub answers.
    /// </summary>
    public async Task EnsureReleaseNotesAsync(CancellationToken ct = default)
    {
        if (_releaseNotesChecked || PendingReleaseNotes is not null) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_releaseNotesChecked || PendingReleaseNotes is not null) return;
            var seen = await settings.GetAsync(SeenVersionKey, "", ct);
            if (string.IsNullOrWhiteSpace(seen) && !install.DatabaseExistedAtStartup)
            {
                // A genuinely fresh install: remember its own version as
                // the baseline. An existing database without a marker is an
                // upgrade from before this dialog existed.
                await settings.SetAsync(SeenVersionKey, AppInfo.Version, ct);
                _releaseNotesChecked = true;
                return;
            }
            if (!await EnabledAsync(ct)) return;
            if (!string.IsNullOrWhiteSpace(seen)
                && !UpdateCheckService.IsNewer(AppInfo.Version, seen))
            {
                _releaseNotesChecked = true;
                return;
            }

            var release = await checks.FindReleaseAsync(AppInfo.Version, ct);
            PendingReleaseNotes = release is null
                ? new ReleaseNotesInfo(AppInfo.Version, null, null, AppInfo.ReleasesPage)
                : new ReleaseNotesInfo(
                    AppInfo.Version, release.ReleaseName, release.ReleaseBody,
                    release.ReleaseUrl ?? AppInfo.ReleasesPage);
            _releaseNotesChecked = true;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: try again on the next start.
        }
        catch (Exception ex)
        {
            // The updater must never take the app down.
            logger.LogDebug(ex, "Release notes check failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks for a newer release and notifies once per version. The check
    /// runs at startup and daily; Settings can call it after enabling the
    /// preference.
    /// </summary>
    public async Task CheckAndNotifyAsync(CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct)) return;
        await _gate.WaitAsync(ct);
        try
        {
            var result = await checks.CheckAsync(ct);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.LatestVersion))
                return;
            Available = result;
            Changed?.Invoke();

            if (!UpdateCheckService.IsNewer(result.LatestVersion, AppInfo.Version))
                return;

            var notified = await settings.GetAsync(NotifiedVersionKey, "", ct);
            if (string.Equals(notified, result.LatestVersion, StringComparison.Ordinal))
                return;

            await settings.SetAsync(NotifiedVersionKey, result.LatestVersion, ct);
            notifications.Background(
                $"MelodyBridge v{result.LatestVersion} is available",
                "info",
                "Release notes",
                result.ReleaseUrl ?? AppInfo.ReleasesPage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the next start checks again.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Update check failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the what's-new dialog and remembers the running version.</summary>
    public async Task DismissReleaseNotesAsync(CancellationToken ct = default)
    {
        PendingReleaseNotes = null;
        await settings.SetAsync(SeenVersionKey, AppInfo.Version, ct);
        Changed?.Invoke();
    }

    /// <summary>True when the user allows the automatic GitHub check.</summary>
    public Task<bool> EnabledAsync(CancellationToken ct = default)
        => settings.GetBoolAsync(EnabledKey, true, ct);
}
