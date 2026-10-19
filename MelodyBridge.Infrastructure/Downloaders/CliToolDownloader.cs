using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// One tool-specific piece of work: the arguments for the CLI and, when the
/// request cannot run at all (a foreign URL, a quality band nothing fits),
/// the honest error that stops the run before any process starts.
/// </summary>
public sealed record CliToolRequest(IReadOnlyList<string> Arguments, string? Error = null);

/// <summary>
/// Base for downloader plugins driven by a command line tool the user
/// owns (qobuz-dlp, deemix, tiddl). Every such plugin repeats the same
/// mechanics: resolve the command, run one serialized process in a private
/// work folder, pick the produced audio file up, move it into the playlist
/// folder, sniff the bytes and write the tags.
///
/// A subclass supplies its identity, its command defaults, the work
/// folder, the tool-specific arguments and config seeding. The gate is per
/// instance, so one plugin never blocks another and two test instances
/// never share state.
/// </summary>
public abstract class CliToolDownloader : IDownloader, IPluginAuthStatusProvider, IPluginAvailabilityRefresh
{
    // One CLI run at a time per plugin: the tools write into shared
    // config/database folders and serial runs keep those clean.
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly AvailabilityCache _availability = new();

    protected CliToolDownloader(IPluginConfig config, ILogger logger)
    {
        Config = config;
        Logger = logger;
    }

    public abstract string Id { get; }
    public abstract string Name { get; }

    /// <summary>
    /// The plugin's plain-language description for the Plugins card.
    /// Abstract here on purpose: IDownloader's default members map only
    /// for the class that lists the interface, so a member declared in a
    /// subclass alone would silently read as the empty default, hiding
    /// the card text, the settings panel and the failure hint.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>The plugin's own settings fields; abstract for the same mapping reason.</summary>
    public abstract IReadOnlyList<PluginConfigField> ConfigFields { get; }

    public abstract PluginCapabilities Capabilities { get; }

    /// <summary>
    /// Why the last availability check said no, shown on the card instead
    /// of the generic hint. Stored on the base for the same mapping
    /// reason: subclasses write it, readers (the Plugins page) go through
    /// IDownloader.
    /// </summary>
    public string? LastUnavailableReason { get; protected set; }

    /// <summary>
    /// The last login verdict of the tool. Declared here, not in the
    /// subclasses, because the interface mapping trap would otherwise
    /// serve readers the default value.
    /// </summary>
    public PluginAuthStatus AuthStatus { get; protected set; } = PluginAuthStatus.Unknown;

    /// <summary>
    /// Sets the login verdict and its short reason. A changed problem is
    /// logged once; the periodic probes that keep returning the same
    /// verdict stay quiet, and the card, banner and toast carry it.
    /// </summary>
    protected void SetAuth(PluginAuthState state, string? message = null)
    {
        var previous = AuthStatus.State;
        AuthStatus = new PluginAuthStatus(state, message);

        if (previous == state) return;

        if (state is PluginAuthState.Expired
            or PluginAuthState.Rejected
            or PluginAuthState.Blocked
            or PluginAuthState.PlanRestricted)
        {
            Logger.LogWarning("{Tool}: {Reason}", ToolLabel,
                message ?? "the stored login needs attention");
        }
        else if (state == PluginAuthState.Valid
                 && previous is PluginAuthState.Expired
                     or PluginAuthState.Rejected
                     or PluginAuthState.Blocked
                     or PluginAuthState.PlanRestricted)
        {
            Logger.LogInformation("{Tool}: the stored login works again", ToolLabel);
        }
        else
        {
            Logger.LogDebug("{Tool}: login state {State}", ToolLabel, state);
        }
    }

    /// <summary>Drops the cached availability verdict after a settings save.</summary>
    public void RefreshAvailability() => _availability.Invalidate();

    public abstract Task<bool> IsAvailableAsync(CancellationToken ct = default);
    public abstract Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default);

    /// <summary>Config store; plugin keys are namespaced by <see cref="IDownloader.Id"/>.</summary>
    protected IPluginConfig Config { get; }

    /// <summary>Logger for the concrete plugin type.</summary>
    protected ILogger Logger { get; }

    // ── The subclass surface ─────────────────────────────────────────────

    /// <summary>Human name used in messages: "qobuz-dlp", "deemix", "tiddl".</summary>
    protected abstract string ToolLabel { get; }

    /// <summary>Command line used when the plugin setting is empty.</summary>
    protected abstract string DefaultCommandLine { get; }

    /// <summary>Config key that overrides the command line.</summary>
    protected abstract string CommandConfigKey { get; }

    /// <summary>Wall-clock cap for one CLI run.</summary>
    protected abstract TimeSpan RunTimeout { get; }

    /// <summary>
    /// The private folder the tool runs in and writes its file to. Created
    /// by the base and deleted after the run, whatever the outcome.
    /// </summary>
    protected abstract Task<string> WorkDirectoryAsync(
        string outputDirectory, string? melodyId, CancellationToken ct);

    /// <summary>
    /// Validates the URL, maps the quality and builds the arguments. No
    /// disk, no process: returning an error stops the run before the
    /// command is even resolved.
    /// </summary>
    protected abstract Task<CliToolRequest> BuildRequestAsync(
        string sourceUrl, DownloadQuality quality, string workDirectory, CancellationToken ct);

    /// <summary>
    /// Seeds the tool's own config (login, token, config file) and returns
    /// the environment the run needs. Runs once per download, after the
    /// command is known to exist and inside the run gate.
    /// </summary>
    protected virtual Task<IReadOnlyDictionary<string, string>?> PrepareEnvironmentAsync(
        CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    /// <summary>Message when the tool exits 0 but produced no audio file.</summary>
    protected virtual string NoFileMessage =>
        $"{ToolLabel} reported success but produced no audio file";

    // ── Shared plumbing for subclasses ───────────────────────────────────

    /// <summary>The configured command line split into executable and leading arguments.</summary>
    protected async Task<(string Executable, IReadOnlyList<string> Arguments)?> CommandAsync(
        CancellationToken ct)
    {
        var configured = await Config.GetAsync(Id, CommandConfigKey, ct);
        var line = string.IsNullOrWhiteSpace(configured) ? DefaultCommandLine : configured.Trim();
        return CliRunner.SplitCommand(line);
    }

    /// <summary>True when the configured command resolves to a real executable.</summary>
    protected async Task<bool> CommandExistsAsync(CancellationToken ct)
    {
        var command = await CommandAsync(ct);
        return command is not null && CliRunner.Exists(command.Value.Executable);
    }

    /// <summary>One config value, trimmed; never null.</summary>
    protected async Task<string> ConfigValueAsync(string key, CancellationToken ct)
        => (await Config.GetAsync(Id, key, ct)).Trim();

    /// <summary>A still-valid availability verdict, when one exists.</summary>
    protected bool TryGetCachedAvailability(out bool available)
        => _availability.TryGet(out available);

    /// <summary>Stores an availability verdict for the short TTL.</summary>
    protected void StoreAvailability(bool available) => _availability.Store(available);

    /// <summary>The last meaningful line of a failed CLI run.</summary>
    public static string DescribeFailure(CliRunner.Result result, string toolLabel)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.Length > 0 ? lines[^1] : "no output";
        if (last.Length > 300) last = last[..300] + "...";
        return $"{toolLabel} failed (exit {result.ExitCode}): {last}";
    }

    /// <summary>
    /// The tool's last meaningful output line, so a run that exits 0
    /// without a file still says why: deemix reports "Track not found at
    /// desired bitrate" that way. Progress lines and deemix's per-track
    /// "Download complete" line are skipped. Internal for tests.
    /// </summary>
    internal static string LastOutputLine(CliRunner.Result result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.EndsWith('%')) continue; // download progress
            if (line.EndsWith("Download complete", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Length > 300 ? line[..300] + "..." : line;
        }
        return "";
    }

    // ── The shared download flow ─────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return Fail("no source URL");

        // The work folder only needs config, never the tool, so the
        // request is built first: a foreign URL or an impossible quality
        // band fails without resolving the command or touching the disk.
        var workDirectory = await WorkDirectoryAsync(outputDirectory, melodyId, ct);
        var request = await BuildRequestAsync(
            sourceUrl, quality ?? DownloadQuality.Any, workDirectory, ct);
        if (request.Error is not null)
            return Fail(request.Error);

        var command = await CommandAsync(ct);
        if (command is null || !CliRunner.Exists(command.Value.Executable))
            return Fail($"the {ToolLabel} command is not configured or not on PATH");

        Directory.CreateDirectory(workDirectory);

        await _runGate.WaitAsync(ct);
        try
        {
            var environment = await PrepareEnvironmentAsync(ct);

            var arguments = new List<string>(command.Value.Arguments);
            arguments.AddRange(request.Arguments);
            Logger.LogDebug("{Tool}: running {Command} {Args}",
                ToolLabel, command.Value.Executable, string.Join(" ", arguments));

            var result = await CliRunner.RunAsync(
                command.Value.Executable, arguments, RunTimeout, ct, environment);
            if (!result.Success)
            {
                var reason = DescribeFailure(result, ToolLabel);
                Logger.LogWarning("{Tool} failed: {Reason}", ToolLabel, reason);
                ObserveFailedRun(result);
                return Fail(reason);
            }

            var produced = CliRunner.FindNewestAudioFile(workDirectory);
            if (produced is null)
            {
                // A tool can exit 0 and still write nothing (deemix stops on
                // a track with no file at the requested tier). Its own last
                // line says more than the generic message.
                var said = LastOutputLine(result);
                return Fail(said.Length > 0
                    ? $"{NoFileMessage}. {ToolLabel} said: {said}"
                    : NoFileMessage);
            }

            var target = FileMove.UniqueTarget(outputDirectory, Path.GetFileName(produced));
            FileMove.MoveAcrossDevices(produced, target);

            // Real bytes only: a JSON error page saved under an audio name
            // must never enter the library.
            var container = AudioSignature.EnsureExtension(target);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(target);
                return Fail($"the {ToolLabel} file is not audio");
            }

            YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
            return new DownloaderDownloadResult(true, container, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Tool} download failed for {Url}", ToolLabel, sourceUrl);
            return Fail(ex.Message);
        }
        finally
        {
            _runGate.Release();
            try { Directory.Delete(workDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A hook for a subclass whose tool can fail because the login went
    /// bad mid-run. The default does nothing: a command failure alone is
    /// not proof that the token is stale.
    /// </summary>
    protected virtual void ObserveFailedRun(CliRunner.Result result) { }

    private static DownloaderDownloadResult Fail(string message)
        => new(false, null, message);
}
