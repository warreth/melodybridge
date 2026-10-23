using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Everything that turns a source into a stored playlist: fetching it
/// (account first, public fallback), reconciling the snapshot, refreshing,
/// importing files and resolving which source provider handles a URL.
/// </summary>
internal sealed class PlaylistSnapshots
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ISourceProvider[] _providers;
    private readonly IAccountSourceProvider[] _accountProviders;
    private readonly SettingsStore _settings;
    private readonly ILogger _logger;
    private readonly TrackLinker? _linker;
    private readonly DataChangeTracker _changes;

    public PlaylistSnapshots(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ISourceProvider[] providers,
        IAccountSourceProvider[] accountProviders,
        SettingsStore settings,
        ILogger logger,
        TrackLinker? linker = null,
        DataChangeTracker? changes = null)
    {
        _dbFactory = dbFactory;
        _providers = providers;
        _accountProviders = accountProviders;
        _settings = settings;
        _logger = logger;
        _linker = linker;
        _changes = changes ?? new DataChangeTracker();
    }

    /// <summary>
    /// Fetches a playlist: the account path first (private playlists, fresh
    /// metadata), the public path as the independent fallback. The public
    /// fetcher needs no account and keeps working when the auth flow or the
    /// account breaks.
    ///
    /// Spotify can have two connections at once: the official OAuth app and
    /// the unofficial web session. Both are tried, official first, in that
    /// order regardless of how DI listed them.
    /// </summary>
    private async Task<Playlist> FetchWithAccountFallbackAsync(
        ISourceProvider provider, string sourceIdentifier, CancellationToken ct)
    {
        var candidates = _accountProviders
            .Where(a => a.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase)
                        || provider.Platform == Platform.Spotify
                        && a is SpotifyUnofficialAccountProvider)
            .OrderBy(a => a is SpotifyAccountProvider ? 0
                : a is SpotifyUnofficialAccountProvider ? 1
                : 2)
            .ToList();

        foreach (var account in candidates)
        {
            try
            {
                // A settings read can fail too; the public path must not be
                // lost because one account could not answer that question.
                if (!await account.IsConnectedAsync(ct)) continue;

                var viaAccount = provider.Platform switch
                {
                    Platform.Spotify when account is SpotifyAccountProvider spotify =>
                        await spotify.TryGetPlaylistViaAccountAsync(
                            SpotifySourceProvider.ExtractPlaylistId(sourceIdentifier), ct),
                    Platform.Spotify when account is SpotifyUnofficialAccountProvider unofficial =>
                        await unofficial.TryGetPlaylistViaAccountAsync(
                            SpotifySourceProvider.ExtractPlaylistId(sourceIdentifier), ct),
                    Platform.YouTubeMusic when account is YouTubeAccountProvider youtube =>
                        await youtube.TryGetPlaylistViaAccountAsync(
                            YouTubeSourceProvider.GetPlaylistId(sourceIdentifier), ct),
                    _ => null,
                };
                if (viaAccount is not null) return viaAccount;

                _logger.LogInformation(
                    "Account fetch for '{Source}' through {Account} returned nothing; trying the next path",
                    sourceIdentifier, account.Name);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "Account fetch for '{Source}' through {Account} failed ({Message}); trying the next path",
                    sourceIdentifier, account.Name, ex.Message);
            }
        }

        return await provider.GetPlaylistAsync(sourceIdentifier);
    }

    /// <summary>
    /// Fetch a playlist live from its source platform and persist it.
    /// Reuses the existing row when the playlist was saved before (match on
    /// platform + external ID), so re-syncing updates instead of duplicating.
    /// </summary>
    public async Task<PlaylistEntity> AddOrRefreshAsync(string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
        => (await AddOrRefreshDetailedAsync(sourceUrl, targetDirectory, ct)).Playlist;

    /// <summary>
    /// Same fetch, plus whether the playlist was newly saved or updated an
    /// existing row. The add form says "updated" instead of "saved" with it.
    /// </summary>
    public async Task<(PlaylistEntity Playlist, bool NewlySaved)> AddOrRefreshDetailedAsync(
        string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
    {
        // Liked songs have no public URL: they are fetched through the
        // account they belong to, never through a public provider whose
        // ID extractor would reject the marker and fail every refresh.
        if (ResolveLikedAccount(sourceUrl) is { } likedAccount)
        {
            var liked = await likedAccount.GetLikedPlaylistAsync(ct);
            var likedPlatform = likedAccount.Name switch
            {
                SpotifyAccountProvider.ProviderName => Platform.Spotify,
                YouTubeAccountProvider.ProviderName => Platform.YouTubeMusic,
                _ => throw new InvalidOperationException($"Unknown account '{likedAccount.Name}'."),
            };
            return await SaveSnapshotAsync(likedPlatform, liked, sourceUrl, targetDirectory, ct);
        }

        var provider = ResolveProvider(sourceUrl)
            ?? throw new InvalidOperationException(
                $"No source provider can handle '{sourceUrl}'. Supported: {string.Join(", ", _providers.Select(p => p.Name))}");

        var playlist = await FetchWithAccountFallbackAsync(provider, sourceUrl, ct);
        return await SaveSnapshotAsync(
            provider.Platform, playlist, sourceUrl, targetDirectory, ct);
    }

    /// <summary>
    /// The logged-in user's liked songs, imported through their account.
    /// </summary>
    public async Task<PlaylistEntity> AddOrRefreshLikedAsync(
        string providerName, string? targetDirectory = null, CancellationToken ct = default)
    {
        var account = _accountProviders.FirstOrDefault(
            a => a.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No account provider '{providerName}'.");

        var playlist = await account.GetLikedPlaylistAsync(ct);
        return (await SaveSnapshotAsync(
            playlist: playlist,
            providerPlatform: account.Name switch
            {
                SpotifyAccountProvider.ProviderName => Platform.Spotify,
                SpotifyUnofficialAccountProvider.ProviderName => Platform.Spotify,
                YouTubeAccountProvider.ProviderName => Platform.YouTubeMusic,
                _ => throw new InvalidOperationException($"Unknown account '{providerName}'."),
            },
            sourceUrl: playlist.SourceUrl,
            targetDirectory: targetDirectory,
            ct: ct)).Entity;
    }

    /// <summary>
    /// Imports playlists parsed from a user-provided file (Exportify CSV
    /// or a Spotify privacy export). Each one goes through the normal
    /// snapshot pipeline, so re-imports update instead of duplicating.
    /// Returns (importedPlaylists, totalTracks).
    /// </summary>
    public async Task<(int playlists, int tracks)> ImportFileAsync(
        ImportedFile file, string? targetDirectory = null, CancellationToken ct = default)
    {
        var imported = 0;
        var totalTracks = 0;

        foreach (var incoming in file.Playlists)
        {
            if (incoming.Tracks.Count == 0) continue;

            // Stable identity per imported playlist: re-uploading the
            // same file refreshes (the YourLibrary liked-songs import
            // always lands on spotify:import:liked, kept apart from the
            // OAuth spotify:liked). Manual imports have no live source
            // to pull from, so they count as Unknown platform: the cards
            // say Imported, and refresh/auto-sync leave them alone.
            var slug = PlaylistRules.Slugify(incoming.Name);
            await SaveSnapshotAsync(
                providerPlatform: Platform.Unknown,
                playlist: new Playlist
                {
                    Id = slug,
                    Name = incoming.Name,
                    Owner = incoming.Owner,
                    Tracks = incoming.Tracks.ToList(),
                },
                sourceUrl: $"spotify:import:{slug}",
                targetDirectory: targetDirectory,
                ct: ct);
            imported++;
            totalTracks += incoming.Tracks.Count;
        }

        _logger.LogInformation("File import ({Kind}): {Playlists} {PlaylistWord}, {Tracks} {TrackWord}",
            file.Kind, imported, imported == 1 ? "playlist" : "playlists",
            totalTracks, totalTracks == 1 ? "track" : "tracks");
        return (imported, totalTracks);
    }

    /// <summary>
    /// Imports every playlist of one uploaded file (a zip holds several).
    /// The counts are summed; each playlist keeps the identity of its own
    /// file, so a re-upload still updates instead of duplicating.
    /// </summary>
    public async Task<(int playlists, int tracks)> ImportFilesAsync(
        IEnumerable<ImportedFile> files, string? targetDirectory = null, CancellationToken ct = default)
    {
        var playlists = 0;
        var tracks = 0;
        foreach (var file in files)
        {
            var (filePlaylists, fileTracks) = await ImportFileAsync(file, targetDirectory, ct);
            playlists += filePlaylists;
            tracks += fileTracks;
        }
        return (playlists, tracks);
    }

    private async Task<(PlaylistEntity Entity, bool NewlySaved)> SaveSnapshotAsync(
        Platform providerPlatform, Playlist playlist, string? sourceUrl,
        string? targetDirectory = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.SourcePlatform == providerPlatform &&
                                     p.ExternalId == playlist.Id, ct);

        var isNew = entity is null;
        entity ??= new PlaylistEntity
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = sourceUrl ?? playlist.Id ?? "unknown-source",

            SourcePlatform = providerPlatform,
        };

        entity.Name = playlist.Name ?? entity.Name;
        entity.Description = playlist.Description;
        entity.ExternalId = playlist.Id;
        entity.CoverImageUrl = playlist.CoverImageUrl;
        entity.Owner = playlist.Owner;

        // The declared total survives the save: when the source fetched
        // fewer tracks than it advertises (a failed page mid-playlist,
        // rate limiting) the mismatch stays visible in the entity and
        // the sync is marked with a warning, never a clean Completed.
        entity.TrackCount = playlist.TrackCount ?? playlist.Tracks?.Count ?? 0;
        entity.LastSyncAt = DateTime.UtcNow;
        var partial = playlist.Warning is { Length: > 0 }
            || playlist.TrackCount is > 0 && playlist.Tracks is { Count: > 0 }
                && playlist.Tracks.Count < playlist.TrackCount;
        entity.LastSyncStatus = partial ? SyncStatus.CompletedWithWarning : SyncStatus.Completed;
        if (partial)
        {
            entity.SyncWarning = playlist.Warning is { Length: > 0 }
                ? playlist.Warning
                : $"partial fetch: got {playlist.Tracks?.Count ?? 0} of {playlist.TrackCount} tracks";
            _logger.LogWarning(
                "Playlist '{Playlist}' synced partially: {Fetched} of {Total} tracks",
                entity.Name, playlist.Tracks?.Count ?? 0, playlist.TrackCount);
        }
        else entity.SyncWarning = null;
        if (targetDirectory is not null)
            entity.TargetDirectory = targetDirectory;
        else if (isNew)
            // Explicit default beats an implicit throw on the first
            // download: a new playlist knows its folder from day one.
            entity.TargetDirectory = await _settings.GetAsync("music_path", "/music", ct);

        // Same for the quality: the app-wide default seeds every new
        // playlist (account import, liked songs, file import), and the
        // playlist page can change it afterwards.
        if (isNew)
        {
            var defaultQuality = await _settings.GetAsync("default_quality", "auto", ct);
            entity.PreferredFormat = PlaylistRules.IsValidFormat(defaultQuality) ? defaultQuality : "auto";
        }

        // A playlist folder the Library can see: every NEW folder becomes a
        // scan location so freshly downloaded files show up in the library.
        if (isNew && !string.IsNullOrWhiteSpace(entity.TargetDirectory))
            await PlaylistQueries.EnsureScanLocationAsync(db, entity.TargetDirectory, ct);

        // Reconcile the snapshot according to the playlist's SyncMode:
        //  Additive: removed-from-source tracks are kept but flagged.
        //  Mirror   : removed-from-source tracks are deleted entirely.
        var mode = PlaylistRules.ParseSyncMode(entity.SyncMode);
        var incoming = playlist.Tracks ?? [];
        var incomingIds = incoming
            .Select(t => t.PlatformSongID?.ID ?? t.SongID?.ID)
            .Where(id => id is not null)
            .ToHashSet();

        // Carry over stable identity, download state and the measured
        // audio facts for tracks that persist across syncs: ExternalId is
        // the join key. A playlist may legally contain the same track
        // twice, so each incoming occurrence takes the next prior row
        // with that id instead of a dictionary throwing on duplicates.
        var priorsByExternalId = entity.Tracks
            .Where(t => t.ExternalId is not null)
            .GroupBy(t => t.ExternalId!)
            .ToDictionary(g => g.Key, g => new Queue<TrackEntity>(g));

        var removed = entity.Tracks
            .Where(t => t.ExternalId is not null && !incomingIds.Contains(t.ExternalId))
            .ToList();

        // A partial fetch did not see the whole playlist, so "absent
        // from source" is not a fact yet: keep those rows attached to
        // the playlist and let the next complete sync do the bookkeeping.
        var withheld = new List<TrackEntity>();
        if (partial && removed.Count > 0)
        {
            _logger.LogWarning(
                "Partial sync for '{Playlist}': keeping {Count} unseen rows until a complete sync",
                entity.Name, removed.Count);
            withheld = removed;
            removed = new List<TrackEntity>();
        }

        if (mode == PlaylistSyncMode.Mirror)
        {
            db.Tracks.RemoveRange(removed);
        }
        else
        {
            // Additive: keep history rows, but detach them from the playlist
            // relationship so re-assigning Tracks below does not delete them.
            foreach (var track in removed)
            {
                track.DownloadStatus = "removed-from-source";
                track.PlaylistSnapshotId = track.Id; // remember origin; relationship dropped below
                entity.Tracks.Remove(track);
            }
        }

        // Update the surviving rows in place. Reusing the entity keeps its
        // id, download state and measured facts, and an unchanged source
        // row costs no write at all: the old delete-everything-and-insert
        // approach rewrote a 3000-track playlist on every refresh and held
        // SQLite's single writer for seconds. Withheld (partial-fetch) rows
        // stay attached and survive untouched.
        var reusedTrackIds = new HashSet<int>();
        var rebuilt = new List<TrackEntity>(incoming.Count + withheld.Count);
        for (var i = 0; i < incoming.Count; i++)
        {
            var source = incoming[i];
            var externalId = source.PlatformSongID?.ID ?? source.SongID?.ID;
            TrackEntity? prior = null;
            if (externalId is not null
                && priorsByExternalId.TryGetValue(externalId, out var priors)
                && priors.Count > 0)
            {
                prior = priors.Dequeue();
            }

            var mapped = PlaylistRules.MapTrack(source, providerPlatform, entity.Id, i);
            mapped.IsLiked = source.IsLiked;
            if (prior is null)
            {
                rebuilt.Add(mapped);
                continue;
            }

            ApplySourceMetadata(prior, mapped);
            reusedTrackIds.Add(prior.Id);
            rebuilt.Add(prior);
        }

        // Rows the source no longer lists (and duplicate leftovers) go.
        // History rows the additive mode detached above are not touched
        // again here.
        var removedIds = removed.Select(t => t.Id).ToHashSet();
        var stale = entity.Tracks
            .Except(withheld)
            .Where(t => !reusedTrackIds.Contains(t.Id) && !removedIds.Contains(t.Id))
            .ToList();
        db.Tracks.RemoveRange(stale);

        rebuilt.AddRange(withheld);
        entity.Tracks = rebuilt;

        if (isNew) db.Playlists.Add(entity);
        await SqliteDatabase.RetryBusyAsync(async () =>
        {
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
        _changes.Bump();

        // The library may already hold some of these tracks (the same
        // songs live in another playlist): link them right away, so this
        // playlist owns its own file names and archive copies before
        // anyone clicks download. A linking problem never fails the sync.
        if (_linker is not null)
        {
            try
            {
                await _linker.LinkSharedTracksAsync(entity.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Linking existing files for '{Playlist}' failed", entity.Name);
            }
        }

        var trackCount = entity.Tracks.Count;
        var platformLabel = providerPlatform == Platform.Unknown ? "Imported" : providerPlatform.ToString();
        _logger.LogInformation("{Action} playlist '{Name}' ({Platform}) with {Count} {TrackWord}",
            isNew ? "Added" : "Refreshed", entity.Name, platformLabel, trackCount,
            trackCount == 1 ? "track" : "tracks");

        return (entity, isNew);
    }

    /// <summary>
    /// Copies the source-owned columns of a fresh mapping onto the row that
    /// already exists. Local columns (download state, file paths, measured
    /// audio facts, review flags) are left alone. Source fields that can
    /// disappear in a partial fetch only overwrite a stored value when the
    /// source has one.
    /// </summary>
    private static void ApplySourceMetadata(TrackEntity row, TrackEntity source)
    {
        row.ExternalId = source.ExternalId;
        row.ExternalPlatform = source.ExternalPlatform;
        row.Title = source.Title;
        row.Artist = source.Artist;
        row.DurationMs = source.DurationMs;
        row.SourceUrl = source.SourceUrl;
        row.Platform = source.Platform;
        row.Position = source.Position;
        row.IsLiked = source.IsLiked;
        row.PlaylistSnapshotId = null;
        row.Album = source.Album ?? row.Album;
        row.AlbumArtist = source.AlbumArtist ?? row.AlbumArtist;
        row.CoverUrl = source.CoverUrl ?? row.CoverUrl;
        row.Lyrics = source.Lyrics ?? row.Lyrics;
        // A resolved ISRC survives a refresh that does not carry one
        // (embed scrape, YouTube).
        row.Isrc = source.Isrc ?? row.Isrc;
        // The measured media type describes the file on disk, so it wins
        // over the source's value when the row already has one.
        if (string.IsNullOrEmpty(row.MediaType))
            row.MediaType = source.MediaType;
        // MelodyId deliberately stays: a legacy mb-{guid} tag keeps
        // matching the file on disk.
    }

    /// <summary>
    /// Brings an exported manual-import snapshot back: a row whose source
    /// is "spotify:import:" has no live provider, so it is restored as the
    /// snapshot it left as (Platform.Unknown, its own tracks), with the
    /// same update-instead-of-duplicate behavior the file import has.
    /// </summary>
    public async Task RestoreManualSnapshotAsync(
        PlaylistEntity exported, CancellationToken ct = default)
    {
        var slug = exported.ExternalId
            ?? PlaylistRules.Slugify(exported.Name);
        await SaveSnapshotAsync(
            providerPlatform: Platform.Unknown,
            playlist: new Playlist
            {
                Id = slug,
                Name = exported.Name,
                Owner = exported.Owner,
                Tracks = (exported.Tracks is { Count: > 0 } rows ? rows : [])
                    .Select(t => new Track
                    {
                        Title = t.Title,
                        Artist = t.Artist,
                        Album = t.Album,
                        AlbumArtist = t.AlbumArtist,
                        Duration = t.DurationMs is > 0
                            ? TimeSpan.FromMilliseconds(t.DurationMs.Value)
                            : null,
                    })
                    .ToList(),
            },
            sourceUrl: exported.SourceUrl,
            targetDirectory: exported.TargetDirectory,
            ct: ct);
    }

    /// <summary>
    /// Re-fetch an already persisted playlist from its source.</summary>
    public Task<PlaylistEntity> RefreshAsync(string playlistId, CancellationToken ct = default)
    {
        return RefreshInternal(playlistId, null, ct);
    }

    public Task<PlaylistEntity> RefreshAsync(string playlistId, string targetDirectory, CancellationToken ct = default)
    {
        return RefreshInternal(playlistId, targetDirectory, ct);
    }

    private async Task<PlaylistEntity> RefreshInternal(string playlistId, string? targetDirectory, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

        if (existing.IsManualImport)
            throw new InvalidOperationException(
                $"'{existing.Name}' was imported from a file. Re-import the file to update it.");

        try
        {
            var refreshed = await AddOrRefreshAsync(existing.SourceUrl, targetDirectory, ct);
            return refreshed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refreshing playlist {Id} failed", playlistId);
            await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
            var mark = await db2.Playlists.FindAsync(new object[] { playlistId }, ct);
            if (mark is not null)
            {
                mark.LastSyncStatus = SyncStatus.Failed;
                mark.LastSyncAt = DateTime.UtcNow;
                await db2.SaveChangesAsync(ct);
            }
            throw;
        }
    }

    /// <summary>The account a liked-songs source belongs to, or null.</summary>
    private IAccountSourceProvider? ResolveLikedAccount(string sourceUrl)
    {
        var source = sourceUrl.Trim();
        if (source.Equals(SpotifyAccountProvider.LikedSourceUrl, StringComparison.OrdinalIgnoreCase))
            return _accountProviders.FirstOrDefault(a => string.Equals(
                a.Name, SpotifyAccountProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
        // "LL" is the raw likes list id older rows stored as the source.
        if (source.Equals(YouTubeAccountProvider.LikedSourceUrl, StringComparison.OrdinalIgnoreCase)
            || source.Equals("LL", StringComparison.OrdinalIgnoreCase))
        {
            return _accountProviders.FirstOrDefault(a => string.Equals(
                a.Name, YouTubeAccountProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private ISourceProvider? ResolveProvider(string sourceUrl)
    {
        // Prefer an explicit URL match; fall back to the only provider that
        // can parse the identifier.
        return _providers.FirstOrDefault(p => p.CanHandle(sourceUrl));
    }
}
