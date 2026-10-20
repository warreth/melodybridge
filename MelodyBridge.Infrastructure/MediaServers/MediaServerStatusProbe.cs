using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Answers "is this media server actually connected, and as who?" by probing
/// every stored profile plus the app-wide legacy rows. A URL alone never
/// counts as configured: the config fallbacks carry localhost defaults, so a
/// server needs credentials too before it can read as ready.
/// </summary>
public sealed class MediaServerStatusProbe : IMediaServerStatusProbe
{
    /// <summary>Short enough that a fixed server shows up, long enough that
    /// the dashboard's five second refresh does not hammer three servers.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);

    /// <summary>A dashboard row must answer quickly; a sync can wait longer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly MediaServerProfileStore _profiles;
    private readonly IJellyfinSettings _jellyfin;
    private readonly IPlexSettings _plex;
    private readonly INavidromeSettings _subsonic;
    private readonly IMediaServerDirectoryFactory _directories;
    private readonly ILogger<MediaServerStatusProbe> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MediaServerConnectionStatus>? _cached;
    private DateTime _cachedAt;

    public MediaServerStatusProbe(
        MediaServerProfileStore profiles,
        IJellyfinSettings jellyfin,
        IPlexSettings plex,
        INavidromeSettings subsonic,
        IMediaServerDirectoryFactory directories,
        ILogger<MediaServerStatusProbe> logger)
    {
        _profiles = profiles;
        _jellyfin = jellyfin;
        _plex = plex;
        _subsonic = subsonic;
        _directories = directories;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MediaServerConnectionStatus>> SnapshotAsync(
        bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && Fresh() is { } cachedHit) return cachedHit;
        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && Fresh() is { } cachedInside) return cachedInside;
            var targets = await TargetsAsync(ct);
            var results = await Task.WhenAll(targets.Select(t => ProbeAsync(t, ct)));
            _cached = results;
            _cachedAt = DateTime.UtcNow;
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<MediaServerConnectionStatus>? Fresh()
        => _cached is { } cached && DateTime.UtcNow - _cachedAt < CacheFor ? cached : null;

    /// <summary>One row to probe: a stored profile, or the app-wide legacy
    /// values for a kind that has no profile yet.</summary>
    private sealed record ProbeTarget(
        string Id, string Name, string Kind, string BaseUrl, string Secret,
        string? Username, bool Legacy);

    private async Task<List<ProbeTarget>> TargetsAsync(CancellationToken ct)
    {
        var profiles = await _profiles.GetAllAsync(ct);
        var targets = profiles
            .Select(p => new ProbeTarget(
                p.Id,
                string.IsNullOrWhiteSpace(p.Name) ? MediaServerKinds.Canonical(p.Kind) : p.Name,
                p.Kind,
                p.BaseUrl?.Trim() ?? string.Empty,
                p.ApiKey ?? string.Empty,
                p.Username,
                Legacy: false))
            .ToList();

        // The single-server settings predate profiles. Keep them visible so
        // an install that never opened the Settings page still gets a row;
        // the empty secret from the config fallback keeps it "not configured".
        if (!profiles.Any(p => MediaServerKinds.Canonical(p.Kind) == MediaServerKinds.Jellyfin))
        {
            targets.Add(new ProbeTarget(
                "legacy:jellyfin", "Jellyfin", MediaServerKinds.Jellyfin,
                (await _jellyfin.GetBaseUrlAsync(ct)).Trim(),
                await _jellyfin.GetApiKeyAsync(ct),
                await _jellyfin.GetUserIdAsync(ct),
                Legacy: true));
        }
        if (!profiles.Any(p => MediaServerKinds.Canonical(p.Kind) == MediaServerKinds.Plex))
        {
            targets.Add(new ProbeTarget(
                "legacy:plex", "Plex", MediaServerKinds.Plex,
                (await _plex.GetBaseUrlAsync(ct)).Trim(),
                await _plex.GetApiKeyAsync(ct),
                Username: null,
                Legacy: true));
        }
        if (!profiles.Any(p => MediaServerKinds.IsSubsonic(p.Kind)))
        {
            targets.Add(new ProbeTarget(
                "legacy:subsonic", "Subsonic", MediaServerKinds.Subsonic,
                (await _subsonic.GetBaseUrlAsync(ct)).Trim(),
                await _subsonic.GetPasswordAsync(ct),
                await _subsonic.GetUsernameAsync(ct),
                Legacy: true));
        }
        return targets;
    }

    private async Task<MediaServerConnectionStatus> ProbeAsync(
        ProbeTarget target, CancellationToken ct)
    {
        var kind = MediaServerKinds.Canonical(target.Kind);
        if (string.IsNullOrWhiteSpace(kind)) kind = target.Kind;
        var name = string.IsNullOrWhiteSpace(target.Name) ? kind : target.Name;

        if (!Configured(kind, target))
        {
            return new MediaServerConnectionStatus(
                target.Id, name, kind, target.BaseUrl, MediaServerConnectionState.NotConfigured);
        }

        var directory = _directories.For(kind);
        if (directory is null)
        {
            return new MediaServerConnectionStatus(
                target.Id, name, kind, target.BaseUrl,
                MediaServerConnectionState.Unreachable,
                Reason: "no client for this server kind");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        var connection = new MediaServerConnection(target.BaseUrl, target.Secret, target.Username);
        try
        {
            if (!await directory.TestConnectionAsync(connection, cts.Token))
            {
                return new MediaServerConnectionStatus(
                    target.Id, name, kind, target.BaseUrl,
                    MediaServerConnectionState.Unreachable,
                    Reason: "no answer or login rejected");
            }

            var identity = await directory.IdentifyAsync(connection, cts.Token);
            var account = string.IsNullOrWhiteSpace(identity?.AccountName)
                ? target.Username?.Trim()
                : identity!.AccountName!.Trim();
            return new MediaServerConnectionStatus(
                target.Id, name, kind, target.BaseUrl,
                MediaServerConnectionState.Connected,
                AccountName: string.IsNullOrWhiteSpace(account) ? null : account,
                ServerName: string.IsNullOrWhiteSpace(identity?.ServerName)
                    ? null
                    : identity!.ServerName!.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MediaServerConnectionStatus(
                target.Id, name, kind, target.BaseUrl,
                MediaServerConnectionState.Unreachable,
                Reason: "timed out");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media server probe failed for {Name}", name);
            return new MediaServerConnectionStatus(
                target.Id, name, kind, target.BaseUrl,
                MediaServerConnectionState.Unreachable,
                Reason: "could not connect");
        }
    }

    private static bool Configured(string kind, ProbeTarget target)
        => !string.IsNullOrWhiteSpace(target.BaseUrl)
           && !string.IsNullOrWhiteSpace(target.Secret)
           && (!MediaServerKinds.IsSubsonic(kind) || !string.IsNullOrWhiteSpace(target.Username));
}
