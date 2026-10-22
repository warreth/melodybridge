using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One canonical pipeline from source URL to persisted playlist:
/// resolve provider → fetch live playlist → replace track snapshot in SQLite.
/// Track rows keep their platform IDs and ordering so later stages
/// (downloaders, library scans, sync jobs) can join on them.
/// </summary>
public class PlaylistStore
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ISourceProvider[] _providers;
    private readonly IAccountSourceProvider[] _accountProviders;
    private readonly IDownloadManager _downloadManager;
    private readonly ILogger<PlaylistStore> _logger;
    private readonly SettingsStore _settings;
    private readonly HardLinkService _hardLinks;
    private readonly TrackLinker _linker;
    private readonly DataChangeTracker _changes;
    private readonly IsrcResolver? _isrcResolver;
    private readonly CoverArtFetcher? _coverArt;
    private readonly LyricsResolver? _lyrics;

    // The split-out services. Queries first: the others grow out of it.
    private readonly PlaylistQueries _queries;
    private readonly PlaylistSnapshots _snapshots;
    private readonly PlaylistTransfer _transfer;
    private readonly DownloadPipeline _pipeline;

    /// <summary>
    /// Post-download verification settings: spectral mode and class gap,
    /// length tolerances. Static: one app-wide value that the Server sets
    /// at startup and the Settings page can replace without touching DI
    /// lifetimes.
    /// </summary>
    public static Func<VerificationOptions> Verification { get; set; } = () => new();

    /// <summary>Settings key for the dedup toggle; default on.</summary>
    public const string DedupSettingKey = "dedup_enabled";

    /// <summary>Base format values shown in the UI dropdown.</summary>
    public static readonly string[] FormatOptions = PlaylistRules.FormatOptions;

    /// <summary>Validates a PreferredFormat string; the rules live in <see cref="PlaylistRules"/>.</summary>
    public static bool IsValidFormat(string value) => PlaylistRules.IsValidFormat(value);

    /// <summary>The folder name a playlist's own directory uses (UI hint).</summary>
    public static string FolderNameFor(string? playlistName) => PlaylistRules.FolderNameFor(playlistName);

    /// <summary>
    /// The folder a playlist's downloads land in: the explicit folder, or
    /// the music path with a subfolder named after the playlist (UI hint).
    /// </summary>
    public static string EffectivePlaylistFolder(string? targetDirectory, string musicPath, string? playlistName,
        bool playlistFolders = true)
        => PlaylistRules.ResolvePlaylistFolder(targetDirectory, musicPath, playlistName, playlistFolders);

    /// <summary>The playlist's effective archive folder (UI hint).</summary>
    public static string EffectiveArchiveFolder(string? archiveDirectory, string archivePath, string? playlistName,
        bool playlistFolders = true)
        => PlaylistRules.ResolveArchiveFolder(archiveDirectory, archivePath, playlistName, playlistFolders);

    /// <summary>Path equality as the folder rules apply it (UI hint).</summary>
    public static bool PathsEqual(string? first, string? second)
        => PlaylistRules.PathEquals(first, second);

    public PlaylistStore(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IEnumerable<ISourceProvider> providers,
        IDownloadManager downloadManager,
        ILogger<PlaylistStore> logger,
        IEnumerable<IAccountSourceProvider>? accountProviders = null,
        SettingsStore? settings = null,
        HardLinkService? hardLinks = null,
        IsrcResolver? isrcResolver = null,
        CoverArtFetcher? coverArt = null,
        LyricsResolver? lyrics = null,
        TrackLinker? linker = null,
        DataChangeTracker? changes = null)
    {
        _dbFactory = dbFactory;
        _providers = providers.ToArray();
        _accountProviders = accountProviders?.ToArray() ?? Array.Empty<IAccountSourceProvider>();
        _downloadManager = downloadManager;
        _logger = logger;
        // Settings live in the same database: the lazily built store reads
        // the same rows the injected singleton would.
        _settings = settings ?? new SettingsStore(dbFactory);
        // The OS hard link wrapper has no state; one instance shared by
        // every store, or a private one for tests that never inject it.
        _hardLinks = hardLinks ?? new HardLinkService();
        // The write stamp the pages poll: one instance app-wide in
        // production, a private one for tests that never wire it.
        _changes = changes ?? new DataChangeTracker();
        // The linking half of dedup (hardlink shared tracks and their
        // archive copies) is one module: DI injects the singleton, tests
        // that never wire one still get a working private instance.
        _linker = linker ?? new TrackLinker(
            dbFactory, _settings, _hardLinks, NullLogger<TrackLinker>.Instance, _changes);
        // Optional: without a resolver, tracks simply download without an
        // ISRC like before.
        _isrcResolver = isrcResolver;
        // Optional download metadata: a cover to embed and lyrics to resolve.
        // Without them the file is tagged exactly as before.
        _coverArt = coverArt;
        _lyrics = lyrics;
        _queries = new PlaylistQueries(
            dbFactory, _settings, coverArt, lyrics, logger, _changes, () => Verification());
        _snapshots = new PlaylistSnapshots(
            dbFactory, _providers, _accountProviders, _settings, logger, _linker, _changes);
        _transfer = new PlaylistTransfer(dbFactory, _queries, _snapshots);
        _pipeline = new DownloadPipeline(
            dbFactory, downloadManager, _settings, _linker,
            isrcResolver, coverArt, lyrics, logger, () => Verification(), _changes);
    }

    public Task<(int downloaded, int failed)> DownloadMissingAsync(
        string playlistId, string? outputDirectoryOverride = null, int limit = int.MaxValue, CancellationToken ct = default)
        => _pipeline.DownloadMissingAsync(playlistId, outputDirectoryOverride, limit, ct);

    public Task<string> DownloadTrackAsync(
        int trackId, CancellationToken ct = default, bool avoidLastProvider = false)
        => _pipeline.DownloadTrackAsync(trackId, ct, avoidLastProvider);

    /// <summary>Candidates every enabled plugin offers for one track; the optional callback streams hits.</summary>
    public Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        int trackId, Action<DownloadCandidate>? onCandidate = null, CancellationToken ct = default)
        => _pipeline.SearchCandidatesAsync(trackId, onCandidate, ct);

    /// <summary>The same search plus the plugins that could not be searched.</summary>
    public Task<SourceSearchOutcome> SearchCandidatesDetailedAsync(
        int trackId, Action<DownloadCandidate>? onCandidate = null, CancellationToken ct = default)
        => _pipeline.SearchCandidatesDetailedAsync(trackId, onCandidate, ct);

    /// <summary>The exact audio facts one candidate's plugin can read before
    /// a download, for the picker's quality line. Null when it cannot know.</summary>
    public Task<CandidateAudioQuality?> ProbeCandidateQualityAsync(
        string pluginId, string sourceUrl, CancellationToken ct = default)
        => _pipeline.ProbeCandidateQualityAsync(pluginId, sourceUrl, ct);

    /// <summary>Downloads one candidate a human picked by hand.</summary>
    public Task<string> DownloadCandidateAsync(
        int trackId, DownloadCandidate candidate, CancellationToken ct = default)
        => _pipeline.DownloadChosenTrackAsync(trackId, candidate, ct);

    /// <summary>Rebuilds the archive copy of one downloaded track. Used by
    /// the row's archive action after a failed or missing copy.</summary>
    public Task<(bool Ok, string? Reason)> RetryArchiveAsync(
        int trackId, CancellationToken ct = default)
        => _pipeline.RetryArchiveAsync(trackId, ct);

    /// <summary>Stamp of the last committed change to the stored library
    /// state; the playlist page reloads when it moves.</summary>
    public long DataVersion => _changes.Version;

    /// <summary>
    /// Links the tracks of a playlist that already exist elsewhere in the
    /// library, hardlink and archive copy included, without downloading.
    /// The add/refresh path runs this itself; it is public so a repair
    /// pass (or a test) can insist on it.
    /// </summary>
    public Task<int> LinkSharedTracksAsync(string playlistId, CancellationToken ct = default)
        => _linker.LinkSharedTracksAsync(playlistId, ct);

    public Task<DedupMatch?> FindDedupCandidateAsync(
        string melodyId, int excludingTrackId, CancellationToken ct = default)
        => _pipeline.FindDedupCandidateAsync(melodyId, excludingTrackId, ct);

    public Task<bool> ResolveReviewAsync(int trackId, bool hardlinkExisting, CancellationToken ct = default)
        => _pipeline.ResolveReviewAsync(trackId, hardlinkExisting, ct);

    public Task<bool> ResolveQualityReviewAsync(
        int trackId, QualityReviewAction action, CancellationToken ct = default)
        => _pipeline.ResolveQualityReviewAsync(trackId, action, ct);

    /// <summary>
    /// Maps a playlist's PreferredFormat to the waterfall quality. The rules
    /// live in <see cref="PlaylistRules"/>; this keeps the name the pages
    /// and tests call.
    /// </summary>
    public static DownloadQuality ParseQuality(string? preferredFormat)
        => PlaylistRules.ParseQuality(preferredFormat);

    public Task<List<PlaylistEntity>> GetAllAsync(CancellationToken ct = default)
        => _queries.GetAllAsync(ct);

    public Task<Dictionary<string, (int downloaded, int failed, int total, long sizeBytes, int attention)>> GetDownloadStatsAsync(CancellationToken ct = default)
        => _queries.GetDownloadStatsAsync(ct);

    public Task<HashSet<string>> GetSavedExternalIdsAsync(
        Platform platform, CancellationToken ct = default)
        => _queries.GetSavedExternalIdsAsync(platform, ct);

    /// <summary>Pins or unpins a playlist for the top of the list.</summary>
    public Task SetPinnedAsync(string playlistId, bool pinned, CancellationToken ct = default)
        => _queries.SetPinnedAsync(playlistId, pinned, ct);

    public Task<bool> RemoveTrackAsync(string playlistId, int trackId, bool deleteFile, CancellationToken ct = default)
        => _queries.RemoveTrackAsync(playlistId, trackId, deleteFile, ct);

    /// <summary>Deletes a track's files but keeps the row, back to pending.</summary>
    public Task<TrackFileDeleteResult> DeleteTrackFilesAsync(
        string playlistId, int trackId, CancellationToken ct = default)
        => _queries.DeleteTrackFilesAsync(playlistId, trackId, ct);

    /// <summary>
    /// One playlist with its track snapshot, with only the columns the
    /// pages and the exports read. See
    /// <see cref="PlaylistQueries.GetForDisplayAsync"/>.
    /// </summary>
    public Task<PlaylistEntity?> GetForDisplayAsync(string id, CancellationToken ct = default)
        => _queries.GetForDisplayAsync(id, ct);

    /// <summary>The files and bytes a playlist deletion with files touches.</summary>
    public Task<(int Files, long Bytes)> GetDeleteImpactAsync(
        string playlistId, CancellationToken ct = default)
        => _queries.GetDeleteImpactAsync(playlistId, ct);

    public Task<bool> DeleteAsync(string playlistId, bool deleteFiles = false, CancellationToken ct = default)
        => _queries.DeleteAsync(playlistId, deleteFiles, ct);

    public Task UpdateSettingsAsync(string playlistId, string? name, bool autoSync, int? intervalMinutes, string? targetDirectory, CancellationToken ct = default)
        => _queries.UpdateSettingsAsync(playlistId, name, autoSync, intervalMinutes, targetDirectory, ct);

    public Task UpdateSettingsAsync(string playlistId, string? name, bool autoSync, int? intervalMinutes, string? targetDirectory, PlaylistSyncMode? syncMode, string? preferredFormat = null, CancellationToken ct = default)
        => _queries.UpdateSettingsAsync(playlistId, name, autoSync, intervalMinutes, targetDirectory, syncMode, preferredFormat, ct);

    public virtual Task UpdateScheduleAsync(
        string playlistId, string? name, ScanSchedule schedule,
        string? targetDirectory = null, PlaylistSyncMode? syncMode = null,
        string? preferredFormat = null, string? archiveDirectory = null,
        string? archiveFormat = null, bool? autoDownload = null,
        bool? strictQuality = null, bool? allowContainerChange = null,
        bool? archiveEnabled = null, CancellationToken ct = default)
        => _queries.UpdateScheduleAsync(playlistId, name, schedule, targetDirectory, syncMode, preferredFormat, archiveDirectory, archiveFormat, autoDownload, strictQuality, allowContainerChange, archiveEnabled, ct);

    public Task<List<PlaylistEntity>> GetDueForAutoSyncAsync(CancellationToken ct = default)
        => _queries.GetDueForAutoSyncAsync(ct);

    /// <summary>Stamps an auto-sync attempt (success or failure).</summary>
    public Task RecordSyncAttemptAsync(string playlistId, CancellationToken ct = default)
        => _queries.RecordSyncAttemptAsync(playlistId, ct);

    public virtual Task<(int updated, int skipped, int failed)> RetagLibraryAsync(CancellationToken ct = default)
        => _queries.RetagLibraryAsync(ct);

    public Task<(int recomputed, int missing, int flagged)> RecomputeMissingFactsAsync(CancellationToken ct = default)
        => _queries.RecomputeMissingFactsAsync(ct);

    public Task<PlaylistEntity> AddOrRefreshAsync(string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
        => _snapshots.AddOrRefreshAsync(sourceUrl, targetDirectory, ct);

    public Task<(PlaylistEntity Playlist, bool NewlySaved)> AddOrRefreshDetailedAsync(
        string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
        => _snapshots.AddOrRefreshDetailedAsync(sourceUrl, targetDirectory, ct);

    public Task<PlaylistEntity> AddOrRefreshLikedAsync(
        string providerName, string? targetDirectory = null, CancellationToken ct = default)
        => _snapshots.AddOrRefreshLikedAsync(providerName, targetDirectory, ct);

    public Task<(int playlists, int tracks)> ImportFileAsync(
        ImportedFile file, string? targetDirectory = null, CancellationToken ct = default)
        => _snapshots.ImportFileAsync(file, targetDirectory, ct);

    public Task<(int playlists, int tracks)> ImportFilesAsync(
        IEnumerable<ImportedFile> files, string? targetDirectory = null, CancellationToken ct = default)
        => _snapshots.ImportFilesAsync(files, targetDirectory, ct);

    public Task<PlaylistEntity> RefreshAsync(string playlistId, CancellationToken ct = default)
        => _snapshots.RefreshAsync(playlistId, ct);

    public Task<PlaylistEntity> RefreshAsync(string playlistId, string targetDirectory, CancellationToken ct = default)
        => _snapshots.RefreshAsync(playlistId, targetDirectory, ct);

    public Task<string> ExportAsync(IEnumerable<string>? playlistIds = null, CancellationToken ct = default)
        => _transfer.ExportAsync(playlistIds, ct);

    public Task<byte[]> ExportCsvAsync(string playlistId, CancellationToken ct = default)
        => _transfer.ExportCsvAsync(playlistId, ct);

    public Task<byte[]> ExportCsvZipAsync(IEnumerable<string> playlistIds, CancellationToken ct = default)
        => _transfer.ExportCsvZipAsync(playlistIds, ct);

    public Task<int> ImportAsync(string json, CancellationToken ct = default)
        => _transfer.ImportAsync(json, ct);
}
