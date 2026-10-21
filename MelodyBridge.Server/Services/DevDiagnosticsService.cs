using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;

namespace MelodyBridge.Server.Services;

/// <summary>Runtime facts the dev panel shows; all values are read live.</summary>
public sealed record DevSystemFacts(
    string AppVersion,
    string EnvironmentName,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    DateTimeOffset StartedAt,
    TimeSpan Uptime,
    string? DatabasePath,
    long DatabaseBytes,
    long WalBytes,
    long ShmBytes,
    string MusicPath,
    string ArchivePath);

/// <summary>Row counts from the real SQLite database.</summary>
public sealed record DevDatabaseCounts(
    int Playlists,
    int Tracks,
    int Downloaded,
    int Failed,
    int SyncJobs,
    int ScanLocations,
    int Settings);

/// <summary>
/// The result of one plugin availability probe. <paramref name="ElapsedMs"/>
/// is the real wall time the plugin took, so a slow provider is visible.
/// </summary>
public sealed record DevPluginHealth(
    string Id,
    string Name,
    bool Enabled,
    bool Available,
    string? Reason,
    long ElapsedMs);

/// <summary>
/// Read-only diagnostics for the dev panel: runtime and database facts,
/// plugin probes and a copyable report. Everything here comes from the
/// running process, the real SQLite file or the plugins themselves; no
/// placeholder values and no secrets.
/// </summary>
public sealed class DevDiagnosticsService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly IDownloaderRegistry _registry;
    private readonly ILogCollector _logs;
    private readonly IHostEnvironment _host;

    public DevDiagnosticsService(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        IDownloaderRegistry registry,
        ILogCollector logs,
        IHostEnvironment host)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _registry = registry;
        _logs = logs;
        _host = host;
    }

    /// <summary>Version, runtime, uptime and the database file it is using.</summary>
    public async Task<DevSystemFacts> GetSystemFactsAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetManyAsync(new[] { "music_path", "archive_path" }, ct);
        var databasePath = await ReadDatabasePathAsync(ct);
        var startedAt = ReadStartTime();
        var version = typeof(DevDiagnosticsService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DevDiagnosticsService).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        // The SDK appends the commit hash after a plus sign; the page wants
        // the version a user can match against a release.
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        return new DevSystemFacts(
            AppVersion: version,
            EnvironmentName: _host.EnvironmentName,
            Runtime: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            StartedAt: startedAt,
            Uptime: DateTimeOffset.Now - startedAt,
            DatabasePath: databasePath,
            DatabaseBytes: FileLength(databasePath),
            WalBytes: FileLength(databasePath is null ? null : databasePath + "-wal"),
            ShmBytes: FileLength(databasePath is null ? null : databasePath + "-shm"),
            MusicPath: settings.GetValueOrDefault("music_path", "/music"),
            ArchivePath: settings.GetValueOrDefault("archive_path", ""));
    }

    /// <summary>Exact row counts from the live database.</summary>
    public async Task<DevDatabaseCounts> GetDatabaseCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return new DevDatabaseCounts(
            Playlists: await db.Playlists.CountAsync(ct),
            Tracks: await db.Tracks.CountAsync(ct),
            Downloaded: await db.Tracks.CountAsync(t => t.DownloadStatus == "downloaded", ct),
            Failed: await db.Tracks.CountAsync(t => t.DownloadStatus == "failed", ct),
            SyncJobs: await db.SyncJobs.CountAsync(ct),
            ScanLocations: await db.ScanLocations.CountAsync(ct),
            Settings: await db.DownloaderSettings.CountAsync(ct));
    }

    /// <summary>
    /// Probes every registered plugin in parallel. A plugin that hangs is
    /// cut off after 15 seconds and one that throws is reported with the
    /// exception message; neither takes the rest of the list down.
    /// </summary>
    public async Task<IReadOnlyList<DevPluginHealth>> ProbePluginsAsync(CancellationToken ct = default)
    {
        var plugins = _registry.GetAll().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var probes = plugins.Select(plugin => ProbeOneAsync(plugin, ct)).ToList();
        return await Task.WhenAll(probes);
    }

    private async Task<DevPluginHealth> ProbeOneAsync(IDownloader plugin, CancellationToken ct)
    {
        var enabled = _registry.IsEnabled(plugin.Id);
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var available = await plugin.IsAvailableAsync(timeout.Token);
            watch.Stop();
            return new DevPluginHealth(
                plugin.Id, plugin.Name, enabled, available,
                available ? null : TrimReason(plugin.LastUnavailableReason ?? "reported unavailable"),
                watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            watch.Stop();
            return new DevPluginHealth(
                plugin.Id, plugin.Name, enabled, false,
                $"probe timed out after {ProbeTimeout.TotalSeconds:0} seconds",
                watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new DevPluginHealth(
                plugin.Id, plugin.Name, enabled, false,
                TrimReason(ex.Message), watch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// A plain-text report for a bug report or a support thread. Recent
    /// warnings and errors are included, with the same redaction the Logs
    /// page uses. No environment variables, settings values or tokens.
    /// </summary>
    public string BuildReport(
        DevSystemFacts facts,
        DevDatabaseCounts counts,
        IReadOnlyList<DevPluginHealth> plugins)
    {
        var report = new StringBuilder();
        report.AppendLine("MelodyBridge diagnostics");
        report.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Version: {facts.AppVersion}");
        report.AppendLine($"Environment: {facts.EnvironmentName}");
        report.AppendLine($"Runtime: {facts.Runtime}");
        report.AppendLine($"OS: {facts.OperatingSystem} ({facts.Architecture})");
        report.AppendLine($"Uptime: {FormatUptime(facts.Uptime)}");
        report.AppendLine($"Database: {facts.DatabasePath ?? "unknown"}");
        report.AppendLine($"Database size: {FormatBytes(facts.DatabaseBytes)} (WAL {FormatBytes(facts.WalBytes)}, SHM {FormatBytes(facts.ShmBytes)})");
        report.AppendLine($"Music path: {facts.MusicPath}");
        report.AppendLine($"Archive path: {(string.IsNullOrWhiteSpace(facts.ArchivePath) ? "not configured" : facts.ArchivePath)}");
        report.AppendLine();
        report.AppendLine($"Playlists: {counts.Playlists}");
        report.AppendLine($"Tracks: {counts.Tracks} ({counts.Downloaded} downloaded, {counts.Failed} failed)");
        report.AppendLine($"Sync jobs: {counts.SyncJobs}");
        report.AppendLine($"Scan locations: {counts.ScanLocations}");
        report.AppendLine($"Settings rows: {counts.Settings}");
        report.AppendLine();
        report.AppendLine("Plugins:");
        if (plugins.Count == 0)
        {
            report.AppendLine("  not probed");
        }
        else
        {
            foreach (var plugin in plugins)
            {
                var state = plugin.Available
                    ? "ready"
                    : $"unavailable: {LogRedactor.Redact(plugin.Reason ?? "no reason given")}";
                var enabled = plugin.Enabled ? "" : "disabled, ";
                report.AppendLine($"  [{enabled}{state}] {plugin.Name} ({plugin.Id}), {plugin.ElapsedMs} ms");
            }
        }

        var problems = _logs.GetEntries()
            .Where(e => e.Level >= CoreLogLevel.Warn)
            .Take(25)
            .ToList();
        report.AppendLine();
        report.AppendLine(problems.Count == 0
            ? "Recent problems: none"
            : "Recent problems:");
        foreach (var entry in problems)
        {
            report.AppendLine($"  [{entry.Level}] {entry.Area}/{entry.Category}: {LogRedactor.Redact(entry.Message)}");
            if (!string.IsNullOrWhiteSpace(entry.Detail))
                report.AppendLine($"    {LogRedactor.Redact(entry.Detail)}");
        }

        return report.ToString();
    }

    private async Task<string?> ReadDatabasePathAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connection)) return null;
        var source = new SqliteConnectionStringBuilder(connection).DataSource;
        if (string.IsNullOrWhiteSpace(source) || source == ":memory:") return null;
        return Path.GetFullPath(source);
    }

    private static DateTimeOffset ReadStartTime()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }

    private static long FileLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string TrimReason(string reason)
    {
        var clean = reason.ReplaceLineEndings(" ").Trim();
        return clean.Length <= 300 ? clean : clean[..300] + "...";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
            : uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{uptime.Minutes}m {uptime.Seconds}s";
}
