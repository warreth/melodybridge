# Developer guide

This document explains the architecture, plugin interfaces, DI setup, and
testing practices for MelodyBridge.

## Architecture overview

The solution follows a clean layered architecture:

```
MelodyBridge.Core              : Interfaces, contracts, enums, models (no dependencies)
       ↕
MelodyBridge.Infrastructure    : Implementations: downloaders, scanners, taggers, media servers
       ↕
MelodyBridge.Application       : Orchestration: SyncEngine, DownloadManager, DI extension methods
       ↕
MelodyBridge.Server            : ASP.NET Blazor web UI + REST API controllers
MelodyBridge.UI.Components     : Shared Blazor components
       ↕
MelodyBridge.Tests             : NUnit test suite targeting all layers
```

### Project dependencies

| Project | References |
|---|---|
| Core | *(none)* |
| Infrastructure | Core |
| Application | Core, Infrastructure |
| Server | Core, Application, Infrastructure, UI.Components |
| Tests | Core, Infrastructure, Application, Server |

The optional Photino desktop wrapper lives in
[warreth/melodybridge-desktop](https://github.com/warreth/melodybridge-desktop),
outside this solution.

### Styling the UI

The whole UI draws from one stylesheet, `MelodyBridge.Server/wwwroot/app.css`, plus
a small `.razor.css` file next to a component for its own layout. That sheet is
modular: every block sits under a section comment and holds one concern.

- **Tokens** in `:root` (and the light theme override) carry every color, radius
  and shadow. Use them, never a raw color, so theme and accent switching keep
  working.
- **Cards**: `.panel-card` is a flat card for content and status, `.settings-card`
  is the raised surface for anything that holds settings (the Settings tabs, the
  plugin panel, the playlist options, the Advanced page, the job wizard). A
  settings card sits above the page and carries `--shadow-raised`, so it reads as
  its own block instead of blending into the background.
- **Inside a card**: `.settings-section` groups related fields behind an eyebrow,
  `.field` or the `.form-grid label` pattern lays out one field (label, control,
  `.field-hint` note), `.hint-box` is a note, `.toggle-row` and
  `.toggle-switch.wide` are switch rows, `.flex-actions` (with `.end` when it
  closes a form) holds buttons.
- **Page styles stay small**: a `.razor.css` may place its own blocks, but a
  surface, a field or a button always comes from the shared sheet. Inline styles
  are only for values the server computes, like a progress width.

### The data flow

```
Spotify playlist URL
   → SpotifySourceProvider (embed page scraping, no API key)
   → PlaylistStore (SQLite snapshot, deterministic MelodyIds, sync modes)
   → DownloadMissingAsync → IsrcResolver (Deezer) → DownloadManager waterfall
   → MP3 file + title/artist tags + MELODY_ID written by the manager
   → post-download verification: broken files fail, doubtful files flag
   → LibraryScanner (reads tags, keeps DB current as files move)
   → SyncJobRunner → M3uGenerator (#EXTINF) or IMediaServerSync (Jellyfin, Plex, Subsonic)

The MELODY_ID in the file is the source of truth and the database is a
cache over it: see Track identity below. ISRC is the matching signal:
see ISRC below.
```

## Core interfaces

### `IDownloader`: download plugins

```csharp
public interface IDownloader
{
    string Id { get; }
    string Name { get; }
    string Description => string.Empty;
    PluginCapabilities Capabilities { get; }   // required, no default
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default);
    Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default);
}
```

`TrackQuery` is one record carrying everything a plugin may search on:
artist, title, quality, the playlist's advertised duration and the ISRC
when known. A record instead of a widening parameter list, so the next
matching signal is one property, not another signature change. Plugins
rank their candidates with `SearchRanker.Rank`: title/artist similarity
plus duration proximity, gross mismatches (a 30s preview for a 4-minute
track) are dropped, and candidates that add an unrequested modifier
(karaoke, cover, remix, live cut) are never offered at all. The
runner-ups travel along in `DownloaderSearchHit.Alternatives` so a dead
first choice (deleted video, geo-block) does not lose the plugin.
`DownloadManager` re-checks every candidate before spending bytes.

Plugins whose catalog indexes ISRCs use it as the strongest signal. Qobuz,
Deezer and Jumo search the code itself, and Qobuz, Deezer, Jumo, Tidal and
Monochrome accept a hit carrying the exact code even when the catalogue spells
the title differently (the length is still checked). Lucida searches its
services by the code too, but its hits carry no ISRC to verify against, so it
still requires a confident title match. YouTube, SoundCloud, Archive.org and
the DoubleDouble endpoint do not index ISRCs and ignore the field.

`PluginCapabilities` is the plugin's honest manifest: every quality level it
can deliver (container, the bitrate band it arrives in, whether the content is
really lossless) and, per level, whether the plugin can be *asked* for it or
merely hands back what the service decides. It has no default implementation,
so a plugin that does not declare its limits does not compile. The router
filters on this data before any network call and orders the survivors: with
**No filter** the user's Plugins-page order is the order, and with a
constrained request the plugins that control the quality run before the ones
that only report what may arrive. No plugin name, no priority table and no
magic band is involved. A non-strict playlist also walks
`QualityLadder.Build`: the request first, then the best declared level per
other container, stepping down until a source delivers (`DownloadManager`
takes a `DownloadFallback` policy for this); the request's cap is never
exceeded.

Plugins do not write the MELODY_ID tag themselves. `DownloadManager`
writes it once for every plugin after the file passes the quality band;
a plugin's tagging code only handles its own metadata.

A plugin that owns a login implements `IPluginAuthStatusProvider` and keeps
`AuthStatus` current (valid, missing, expired, rejected, blocked or plan
restricted). The default interface member rule applies here too: the property
and its setter live on `CliToolDownloader`, not on the three subclasses. The
`PluginAuthMonitor` singleton is the app-wide register of problems; the
waterfall, the Plugins page and the periodic `PluginAuthWatchService` feed it,
and the sidebar mark, the warning banner and the toast read it. A missing login
is not a red alert, only a login that stopped working is. `AuthStatus` is
separate from `IsAvailableAsync` because a dead service and a stale token need
different words and a different fix. Its optional `Account`, `Plan`, `Tier` and
`ExpiresAtUtc` fields are what the card shows for a working login. Settings that
change a login call `IPluginAvailabilityRefresh.RefreshAvailability` so the
save is probed for real, and the Plugins page probes in parallel with a short
budget per plugin.

The media server status probe answers a different question: is this server
reachable, and as whom. `IMediaServerStatusProbe.SnapshotAsync` waits for the
answer, and `CurrentAsync` does not: it returns the last answer, or the known
servers in a `Checking` state, and the probe runs in the background. The
dashboard uses the second one and redraws the connection rows when the probe
raises `Changed`, so a page load never waits for a server that does not
answer. `MediaServerStatusWatcher` keeps the answer warm in the background.

Qobuz needs two payload quirks in mind: some catalogue answers arrive gzipped
even when the request did not ask for it, and `maximum_sampling_rate` is in kHz
as a decimal (`44.1`). `QobuzClient.ReadBodyAsync` decodes the body and the
parser accepts both kHz and Hz.

"Retry elsewhere" reads `DownloadManager.LastAttemptedPluginId` and stores it as
the track's one-shot `AvoidPluginId`, so the same failing provider is left out
of that retry. The sidebar activity chip is `DownloadActivitySummary.From`, a
pure formatter. Log areas and sources are `LogAreas` and `LogSources`;
`LogCorrelation` stamps entries with the track group the Logs page can group by.
`NotificationService` keeps active toasts and history separately: routine
notices fade after a few seconds, warnings and errors after 12, and both remain
in the bell history.

The store itself is a facade. The implementation lives in four services next to
it: `PlaylistQueries` (reads, lifecycle, settings and the two maintenance
passes), `PlaylistSnapshots` (source fetching, snapshot reconciliation, refresh,
imports), `DownloadPipeline` (claim, dedup, waterfall, tagging, integrity,
archive) and `PlaylistTransfer` (JSON and CSV export/import). `PlaylistRules`
holds the pure rules they share. New behaviour belongs in the service that owns
it; the facade keeps its public surface so pages and tests do not move.

Two reads carry the whole app and are shaped for it. `GetForDisplayAsync`
returns a playlist with its tracks, but only the columns the pages and the
exports read: a track row also holds the resolved lyrics and the cover url, and
a playlist of a few thousand tracks would drag megabytes of text into memory
for a table that never shows them.

`GetDownloadStatsAsync` counts the tracks per playlist for the playlist cards,
and the index on (`PlaylistEntityId`, `DownloadStatus`, `FileSizeBytes`,
`ReviewKind`) holds exactly the columns those counters read, so the group-by
walks the index instead of the table. A SchemaPatcher step adds the index to
older databases.

A plugin that can list more than its ranked winner overrides
`SearchAllAsync` (the yt-dlp backed ones do): the manual source picker shows
those raw candidates, so a hit the ranker refused stays reachable for a human.
The default returns the ranked hit plus its alternatives.

Implement this to add a download source. Built-in plugins:

| Plugin | Id | Source | Notes |
|---|---|---|---|
| `LucidaDownloader` | `lucida` | lucida.to (Tidal, Qobuz, Amazon Music) | High quality rips; needs a Cloudflare solver, otherwise skipped; Qobuz/Amazon are searched by ISRC first, but its hits carry no ISRC, so a confident title match is still required; the load API has no quality parameter, so its manifest is honest-but-uncontrollable |
| `MonochromeDownloader` | `monochrome` | tracks.monochrome.st (official Monochrome API) | Lossless FLAC streams over a plain GET, no account needed; search hits carry an ISRC, and an exact match is accepted as that recording even when the title differs; the base URL is configurable per plugin for mirrors |
| `SlskdDownloader` | `slskd` | Soulseek, through the user's own slskd instance | Lossless FLAC and high-bitrate MP3/AAC from peers; search and transfer run over slskd's REST API with an API key; locked files and undeclared containers are skipped; the finished file moves out of slskd's downloads folder, whose path the plugin reads from slskd's own options; unavailable until the URL and key are set and slskd is logged in |
| `QobuzDownloader` | `qobuz` | qobuz-dlp (shipped in the Docker image) | Lossless and hi-res from a paid Qobuz plan; the search runs natively (bundle scrape plus a user token), the download through the CLI into a hidden per-track folder; the plugin seeds the CLI config (app id, private key, secrets) and its token file, so no interactive login is needed; unavailable until the command and a valid token are found |
| `TidalDownloader` | `tidal` | tiddl (shipped in the Docker image) | Lossless and hi-res from the user's own Tidal session; the search runs against Tidal's v1 API with the token out of tiddl's `auth.json` (a text search that accepts an exact ISRC hit as the recording, even when the title differs), the download through `tiddl download url` with the tier mapped onto the requested band; unavailable until the command and a logged-in tiddl session are found, or when that session expired |
| `DeemixDownloader` | `deemix` | deemix CLI (shipped in the Docker image, runs on Node) | Deezer MP3/FLAC with the user's ARL; the search runs against Deezer's public API, the download through the CLI; the ARL setting is written into deemix's config (through `DEEMIX_DATA_DIR`) before every run, so the CLI never prompts; runs are serialized and the finished file moves out of the downloads folder |
| `JumoDownloader` | `jumo` | jumo-dl.pages.dev (community Qobuz front-end) | No account needed; search and stream descriptors come from the service's guarded API (request-context headers plus ECDH/AES-GCM encrypted responses), the audio comes direct from the signed Qobuz CDN URLs and is AES-CTR decrypted locally; lossless lands as native FLAC, MP3 320 as plain MP3; the service rate limits (429) and can refuse some networks (403), so the plugin reports unavailability instead of hammering it |
| `DoubleDoubleDownloader` | `doubledouble` | us./eu.doubledouble.top | Retired from the shipped order: the site is captcha-gated on every submission, so `IsAvailableAsync` only reports ready when its captcha config says `active: false`. Submit-and-poll rips of direct track URLs; `SearchAsync` returns null by design |
| `SoundCloudDownloader` | `soundcloud` | SoundCloud (via yt-dlp `scsearch`) | Original uploads, often 320 kbps; top-5 ranked; rejects files under 128 kbps |
| `ArchiveOrgDownloader` | `archiveorg` | Internet Archive (public JSON APIs) | Public-domain and community recordings; picks the matching file inside a multi-track item, not the first one; rejects files under 128 kbps |
| `YtDlpDownloader` | `ytdlp` | YouTube (via yt-dlp) | Widest fallback; audio-only best stream, top-5 ranked by match and duration; explicit formats transcode to the requested cap |

Place new implementations in `MelodyBridge.Infrastructure/Downloaders/` and register via `services.AddSingleton<IDownloader, YourPlugin>()`. A plugin that ships its own protocol client or byte-level helpers lives in its own folder next to it instead (`Infrastructure/Lucida/`, `Infrastructure/Jumo/`) and registers the same way. Quality-gate downloads against the requested `DownloadQuality` band so bad rips never enter the library.

Plugins that wrap a command line tool should derive from `CliToolDownloader` (`CliToolDownloader.cs`): it owns command resolution, the private per-track work folder, one serialized process run, picking the produced file up, moving, sniffing, tagging and cleanup. A subclass supplies its id, capabilities, config fields, the work folder, the tool arguments (`BuildRequestAsync`), the tool-specific availability check and, when the tool needs a login or config file, `PrepareEnvironmentAsync`. Qobuz, Deezer and Tidal are built this way; a new CLI source is normally one small file plus a settings entry. `Description` and `ConfigFields` are abstract on the base and `LastUnavailableReason` lives there, all on purpose: `IDownloader`'s default members are bound only for the class that names the interface, so a subclass member alone would silently read as the empty default (an empty settings panel and hint instead of the plugin's real reason).

Every plugin-produced file is sniffed with `AudioSignature` (magic bytes, never the extension) before `DownloadManager` accepts it: an HTML error page or JSON body served under a 200 status is deleted and the waterfall moves on. Plugins that save files themselves delete partial output on failure and cancellation; the yt-dlp-backed plugins keep yt-dlp's resumable `.part` fragments (never final output, and the next attempt resumes from them).

`DownloadQuality` carries a bitrate band: `MinKbps` and `MaxKbps`, both optional, both hard. Search hits whose measured bitrate falls outside the band are skipped, and downloaded files are probed with `BitrateProbe.MeasureKbps` and rejected+deleted when outside it. Unknown bitrates pass the search gate and leave the verdict to the post-download measurement.

`IDownloaderRegistry` manages the plugin waterfall: enable/disable and priority are persisted per plugin in the `ProviderStates` table. Plugin config values, declared via `IDownloader.ConfigFields` (key, label, placeholder and description, plus an `Advanced` flag, a `Secret` flag and select `Options`), persist in the `DownloaderSettings` table under `plugin:{id}:{key}` keys and are edited on the Plugins page behind the card's Settings row, with the advanced fields one click deeper. Plugins read their own values through `IPluginConfig` (the same DB-backed store), so a changed base URL or API key takes effect without a restart. That store keeps one in-memory snapshot of all `plugin:` rows, filled with a single query and updated on every save, so a plugin can read its own settings on a hot path without opening a database connection each time. A database that is replaced from the outside needs the restart the Settings page already asks for. A plugin that can explain its own unavailability sets `LastUnavailableReason` during the check, and the card shows that line instead of the page's generic hint.

`DownloadCoordinator` runs each playlist's downloads in parallel workers and is observable through `Snapshot()`; see `IDownloadManager` below for the per-run work-list semantics.

### `ISourceProvider`: playlist sources

```csharp
public interface ISourceProvider
{
    string Name { get; }
    Platform Platform { get; }
    bool CanHandle(string sourceIdentifier);
    Task<Playlist> GetPlaylistAsync(string sourceIdentifier);
}
```

Implement this to support a new playlist platform. `PlaylistStore` picks the provider whose `CanHandle` accepts the URL. Built-in: `SpotifySourceProvider`, `YouTubeSourceProvider`, `DeezerSourceProvider`, `SoundCloudSourceProvider` and `ListenBrainzSourceProvider` (all registered in `AddMelodyBridge`).

### `IMediaServerSync`: media server targets

```csharp
public interface IMediaServerSync
{
    string Name { get; }
    Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default);
}
```

Implement this to sync playlists to a media server. Built-in targets are resolved by `Name` (`"Jellyfin"`, `"Plex"`, `"Subsonic"`): place implementations in `MelodyBridge.Infrastructure/MediaServers/`: `JellyfinSync.cs`, `PlexSync.cs`, `SubsonicSync.cs`. A job publishes to a list of targets (`SyncJob.Targets`, stored as `SyncJobs.TargetsJson`, legacy single-target rows map from `OutputTarget`): the M3U file plus any number of media server profiles, each resolved fresh per run, and one refused target fails the run without stopping the others. Each has a settings pair (`JellyfinSettings.cs`, `PlexSettings.cs`, `NavidromeSettings.cs`, the Subsonic values keep their historical navidrome_* keys) reading connection values from the settings table, and `IMediaServerDirectory` (`JellyfinUserDirectory.cs`, `PlexSubsonicDirectories.cs`) supplies the wizard's Test connection and user picker. Kind-based lookups go through `IMediaServerDirectoryFactory` (`MediaServerDirectoryFactory.cs`), which knows the aliases ("Navidrome" is Subsonic). Sync jobs pick a named profile from `MediaServerProfileStore` (stored as one JSON list under the `media_server_profiles` settings key) by `SyncJobs.MediaServerProfileId`; `SyncJobRunner` resolves that profile fresh on every run into the per-call `PlaylistOutputOptions.MediaServerConnection` (base URL, credential, optional user id), so editing a profile applies to every job. Jobs created before profiles existed keep their inline `JellyfinServerUrl`/`JellyfinApiKey` values; with neither, the app-wide settings fall back in.

`JellyfinSync` reads the server's audio library (a paged `/Items?IncludeItemTypes=Audio&Fields=Path` walk) and resolves each track locally: by its remapped path first, then by the metadata the same walk already carries (`Name`, with the artist and runtime as tie-breakers) when the path does not line up. Jellyfin returns `Path` for API keys and user tokens alike, so a path miss points at the mount, the remap rules or a rename, not at a permission. Only tracks the walk cannot name, or cannot name unambiguously, reach the metadata search, and the title term goes first so a hit costs one request instead of three. `/Items/ByPath` is tried only when the library walk could not be read at all (older servers). The loaded index is cached for a minute per connection, so the playlists of one sync job share a single walk; a failed walk is not cached and the next playlist retries. A playlist whose members and order already match is not rewritten, and liked songs that are already favorites are not re-posted.

### `IDownloadManager`: the waterfall

```csharp
public interface IDownloadManager
{
    Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default);
    Task<string?> DownloadTrackAsync(TrackQuery query, string outputDirectory, string melodyId,
        DownloadFallback? fallback = null, string? avoidPluginId = null, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        TrackQuery query, CancellationToken ct = default);
    Task<string?> DownloadCandidateAsync(DownloadCandidate candidate, TrackQuery query,
        string outputDirectory, string melodyId, CancellationToken ct = default);
    void ReportProgress(DownloadProgress progress);
    IReadOnlyList<DownloadProgress> SnapshotProgress();
    string? LastPluginId(string melodyId);
    string? LastFailure(string melodyId);
}
```

`SearchCandidatesAsync(query, onCandidate, ct)` and `DownloadCandidateAsync`
are the manual picker's pair: the search runs every enabled plugin's
`SearchAllAsync` at the same time (one request per plugin, so the wait is the
slowest plugin instead of the sum), streams each hit through `onCandidate`, and
applies no acceptance gate and no quality routing (guarded per plugin, best
first); the download fetches exactly the picked candidate. The pick overrides the
ranking, never the sanity: bytes must sniff as audio and the MELODY_ID tag is
written. `DownloadPipeline.DownloadChosenTrackAsync` wraps it with the usual
claim and the shared post-download steps (`CompleteDownloadAsync`: tags,
integrity, enrichment, archive), deletes the row's old primary and archive
name-wise first, and refuses when those cannot go.

`DownloadTrackAsync` routes the enabled plugins through `QualityRouter` first: incompatible plugins are excluded before any network call, survivors are ranked for the request, and the first successful download wins. Without a fallback policy the request is strict: exactly the requested quality or nothing. With a `DownloadFallback` (a non-strict playlist) a miss walks the `QualityLadder`, stepping down through the levels the enabled plugins declare. Every plugin call is individually guarded (a plugin that throws on availability, search or download is skipped, never fatal), each hit is duration-checked before any bytes are spent, and each produced file must sniff as real audio. When every plugin is excluded the track fails fast with "your quality filters excluded every plugin". On success the manager writes the MELODY_ID tag and records `LastPluginId` for the retry path. `DownloadAsync` passes a direct URL to the plugins that can handle it and keeps
the last plugin error so the caller can report it. `ReportProgress` replaces a
manual download's live entry after the file is renamed and tagged, so the
Downloads overview points at the saved path and a failed pick ends as failed
instead of staying on "downloading".

`avoidPluginId` drops one plugin from the whole call, ladder steps included. The quality review uses it when the user picks Retry: the flagged file's `Tracks.SourcePluginId` becomes the next attempt's avoid hint, so the provider that delivered the flagged file gets no second chance at the same track. The hint is one-shot (the store clears it when the attempt starts), and when nothing else can serve the request `LastFailure` says exactly that instead of "no source had this track".

### The post-download verdicts

`PlaylistStore` verifies every downloaded file twice before it counts as done:

- `FileIntegrity.Check` reads the real duration with ffprobe and classifies the file as OK, `Corrupt` (unreadable or truncated: deleted, track failed, retried later) or `DurationMismatch` (the file stays and is flagged).
- `SpectrumAnalyzer.Verify` measures the real bandwidth. An inflated result flags the file; a low but honest bandwidth leaves an advisory `Warning`.

A flag writes `Tracks.ReviewKind` (`length`, `spectrum`, or both joined by `+`, see `TrackReview`) and `ReviewText`. `ResolveQualityReviewAsync` settles it: Keep clears the columns, Retry deletes the primary and archive files, sets `DownloadStatus = "pending"` and copies `SourcePluginId` into the one-shot `AvoidPluginId`. A retry that cannot delete the flagged file refuses and keeps the review open: re-queueing would let the library passes re-link the same bytes as if a fresh download had landed. `PlaylistStore.Verification` carries the live `VerificationOptions` (spectrum mode and class gap, length tolerances); the Server seeds it from the settings table at startup and the Settings page replaces it on save, so a change applies without a restart.

## ISRC: exact matching

`Track.Isrc` and `Tracks.Isrc` carry the International Standard
Recording Code when it is known. Two sources fill it:

1. **The Spotify account path** reads `external_ids.isrc` out of the
   payload it already has: the saved-track object for liked songs and the
   playlist item for playlists. Spotify removed the `GET /v1/tracks?ids=`
   batch endpoint for development-mode apps in February 2026, so there is
   no second pass; a track without the field stays without an ISRC and the
   resolver below gets a second chance per download.
2. **`IsrcResolver`** (Infrastructure) fills the gap for everything else
   (the Spotify embed scrape, YouTube, CSV imports) through Deezer's
   public search API, which returns the ISRC of every hit and needs no
   account. Hits are ranked with the same rules the plugins use
   (title/artist similarity, duration proximity, no unrequested
   modifiers) and only a High-confidence, duration-plausible match is
   accepted: a wrong ISRC is worse than none. Verdicts are memoized per
   process and the resolved value persists on the track row, so a track
   is looked up once.

`PlaylistStore.DownloadClaimedTrackAsync` resolves a missing ISRC right
before the download, persists it with the track status, and passes it
in the `TrackQuery`. A snapshot refresh carries the resolved ISRC over
when the source does not provide one. Qobuz, Deezer and Jumo search by
ISRC first; Tidal and Monochrome accept an exact hit from their text
search; the rest ignore it.

Spotify's public web endpoints were checked and do not expose ISRCs:
the embed page and the track page carry none, `get_access_token` is
blocked (403), and the partner GraphQL API needs an internal client
token and rotating persisted-query hashes. YouTube Music exposes no
ISRC either (yt-dlp's info JSON has no such field), so `IsrcResolver`
uses Deezer's public search as the source of record, with MusicBrainz
as a fallback when Deezer has no confident match (one request per
second, a real User-Agent, no key).

`DownloadCoordinator` runs each playlist with up to `download_max_concurrent` parallel workers (setting, default 2, clamp 1-8, Advanced page). Each run snapshots its work list (pending/failed tracks, Position order) and every track is attempted exactly once per run: a permanently failing track can no longer spin the run forever, and the visible queue plus ETA count the work still in flight. The store's claim (`DownloadTrackAsync(int)`) is still atomic, so a single-track click and a running batch never download the same track twice. A provider cancellation or a transient exception is retried once, then that track is marked failed without stopping the worker. A claim that a dead run left behind (crash, hard shutdown) is reset to pending at startup, and a new run resets only the claims nobody holds: a single-track download that is running keeps its row.

A playlist refresh updates the rows it already has in place: the row id, download state, file paths and measured facts survive, and a source row that did not change costs no write. Rows the source removed still follow the sync mode. Because a refresh can still add or drop rows, auto-sync and the Refresh actions skip a playlist while its run is active; the playlist stays due and is refreshed after the run. The track table renders one page of rows at a time (`TrackTable.PageSize`), keeps the sorted list and queue labels cached between ticks, and asks the page to check archive files only for the visible slice. A 3000-track playlist therefore no longer builds every row and stats every archive path on each poll.

### Other key types

- **`Playlist`**, **`Track`**, **`TrackQuality`**, **`MediaType`**: Core models in `MelodyBridge.Core/Classes.cs`
- **`PlaylistOutputOptions`**: Output path, relative path toggle, path remap dictionary, per-call media server connection, extension remap dictionary (the shared `PlaylistPathMapper` applies prefix remap first, then the extension rule)
- **`TrackEntity`**, **`PlaylistEntity`**, **`ProviderStateRow`**: EF Core entities for persistence
- **`ScanLocationEntity`**, **`SyncJobEntity`**, **`SyncJobRunEntity`**: Library paths and sync job tracking
- **`PlaylistSyncMode`**: `Additive` (removed tracks stay as flagged history) / `Mirror` (local copy matches the source exactly)

## Track identity: MELODY_ID

Every downloaded file carries a `MELODY_ID` tag. The value is
deterministic, derived from the source item by `MelodyIds` (Core):

| Source | Id | Example |
|---|---|---|
| Spotify track | `spotify:{trackId}` | `spotify:4uLU6hMCjMI75M1A2tKUQC` |
| YouTube video | `yt:{videoId}` | `yt:dQw4w9WgXcQ` |
| Internet Archive item | `ia:{identifier}` | `ia:gd1970-06-14` |
| CSV import row | `csv:{16-hex hash}` | SHA-256 of artist, title and duration |
| Id-less fallback | `mbh:{16-hex hash}` | same hash, unknown source |

The tag is the source of truth; the database is a cache over it. Wipe
the database, re-add the same playlists: every row mints the same id
again, the scanner upserts the files by tag, the reconciler relinks
playlist rows to them, and nothing redownloads.

Tag storage, per container format: ID3v2 TXXX frame for MP3, the
Xiph/Vorbis comment field for FLAC, Ogg and Opus, and a
`MELODY_ID={id}` comment marker for MP4 and the rest.
`TaglibHelper.WriteMelodyId` probes Xiph first: creating an
ID3v2 tag inside a FLAC fabricates a block that hijacks storage. The
comment marker path replaces any existing marker instead of appending.
The manager writes the tag, never the plugins, and only after the file
passes the quality band, so no rejected file is ever tagged. The one
exception is a container TagLib cannot write: an opus-in-webm download
without ffmpeg keeps its `.webm` extension, the tag cannot be written,
and the scanner skips that file. The manual download page reads the tag
back and warns on the saved card instead of promising a library entry.

Legacy databases keep their `mb-{guid}` rows: those tags
still match their files. A SchemaPatcher backfill fills the
deterministic id only for rows that have an ExternalId and no MelodyId.

## Hardlink deduplication

Playlist folders are the unit of storage: `PlaylistRules.ResolvePlaylistFolder`
turns a playlist's `TargetDirectory` (plus the `music_path` setting for the
default) into the folder downloads land in. An explicit folder is used as it is;
the app-wide default counts as "no choice" and gets a subfolder named after the
playlist, so every playlist owns its folder, liked songs included. The
`playlist_folders` setting (Default paths, on by default) turns the per-playlist
subfolder off: the root itself is then the folder, and every caller reads the
setting through `SettingsStore`.
`ResolveArchiveFolder` applies the same rule under the archive path, and the
playlist row carries an `ArchiveEnabled` switch that skips the copy entirely when
off. The app writes the archive copy itself, so the row keeps its size in
`ArchiveSizeBytes`: the Library page then reports the size of every archive
copy from the database instead of measuring every file on every visit. A copy
from before that column existed is measured once, in the background, and the
answer is stored.
`LibraryReconciler` moves downloaded files (archive copies included) that
still sit loose under a playlist's base folder into the playlist folder during
startup reconciliation, never overwriting what a destination already holds.
`LibraryPathMover` runs the same guarded move for a whole root when the Music
path or Archive path setting changes: tracked files, playlist folders and scan
locations follow the new root, and a file with an occupied destination keeps
its path and is reported as failed.

The identity tag powers deduplication, and `TrackLinker` is the one
module that performs it: the download claim, the playlist add/refresh
path and `LibraryReconciler` all go through it. A claimed track looks
for another playlist's row with the same MelodyId and a downloaded
file, and compares that file's measured quality with the requested
band. Rows whose `CurrentPath` sits inside an archive root (the
`archive_path` setting or any playlist's `ArchiveDirectory`) are never
candidates: an archive copy cannot become another playlist's primary
file.

- Exact match: `HardLinkService.Create` (P/Invoke `link(2)` on Unix,
  `CreateHardLinkW` on Windows; `File.CreateHardLink` arrives in
  .NET 11) places a second name for the same bytes into this
  playlist's folder. `IsHardLink` marks the row, no network call runs.
  When the destination already names the same inode (a refresh left the
  link in place while the row went back to pending), the attempt accepts
  the existing link instead of failing with EEXIST. An existing name no
  other row points at is replaced by the link, even when its own old
  archive copy raised the link count; a name another row owns is left
  alone. An archive copy the original owns is linked into this playlist's
  own archive folder the same way, but only when it already matches this
  playlist's configured format; a different format gets this playlist's
  own local copy, and only a cross-device link falls back that way.
- Quality difference: the row pauses as `needs-review` with a warning;
  the UI offers "Download new" and "Hardlink existing" (`ResolveReviewAsync`;
  the hardlink button opens a confirmation dialog first). The store never picks
  for the user. "Download new" records a one-shot `Tracks.SkipDedup` flag, so
  the next attempt skips the dedup check once and really fetches a fresh file
  instead of pausing on the same candidate again.
- Cross-device failure (EXDEV): the exception carries the honest
  message, the store logs it and the track downloads normally. A
  `File.Copy` fallback is deliberately forbidden.

The archive copy lands on a fixed name, so that name has an owner. The
database says which rows point at it, and the file that sits on the name
says who owns the bytes: it carries the `MELODY_ID` of the song it
belongs to, and a song that is still in the library keeps its file, even
when the name is spelled with a case the database comparison does not
fold. A file of a song that was removed from the library is a leftover
and does not block the copy. The same rule guards the hard link into a
playlist folder, so both paths judge a name the same way. Two workers
that want the same name take one lock per name (`PathLockTable`), so the
second one waits and then sees what the first one wrote instead of
writing over it, and the delete of a file a rerun replaced takes that
lock as well, so it cannot pull a file away from a worker that is
writing it. Whether two spellings of one name are the same file is the
volume's business, not the operating system's: `FileSystemCase` asks the
volume with a probe and caches the answer per folder, Windows and macOS
included. A volume that cannot be asked is treated as one that folds
case, the answer that never takes a name by accident.

The linking also runs without any download, so a playlist whose tracks
the library already holds owns its own file names from the first
moment: `TrackLinker.LinkSharedTracksAsync` runs right after every
snapshot save (add, refresh, import), and `LibraryReconciler` asks the
linker for a row without a file before it falls back to its MELODY_ID
file index. That index never returns an archive copy (archive folders
and remembered `ArchivePath` values stay out of it), and a
`needs-review` row is left alone by every library pass: only a real
download may pause it for the quality decision, an unprompted pass
leaves a mismatch pending.

The library pass reads a lot of rows, so it reads them the cheap way: a
projection of the columns the pass actually reads (a track row also
carries its lyrics and cover url), one scan of a music folder for all
the playlists that live in it, and one query for the dedup candidates of
a whole playlist instead of one per track. The rows it changes are
attached to the change tracker when they change, so the pass writes the
fields it touched and nothing else.

Deletion is name-wise by hard link semantics: removing one playlist's
row removes that playlist's file name only, and both `RemoveTrackAsync`
and `DeleteAsync` keep any path another row still references (primary
and archive paths both count). The per-track remove dialog on the
playlist page picks between "remove from the playlist", "delete the
file but keep the track" (`DeleteTrackFilesAsync`: the files go, the row
resets to pending) and "delete the file and remove the track"; the latter
takes the primary download and its archive copy. No central pool exists;
playlist folders stay standard.

The setting `dedup_enabled` (default on) toggles the whole feature from
the Advanced page. `Tracks.MelodyId` carries a plain (non-unique) index:
the same source track legitimately lives in several playlists, and a
SchemaPatcher step migrates older databases that had the unique one.

## Database concurrency

SQLite starts in rollback-journal mode, where a read transaction blocks a write. A playlist page reading thousands of rows while a download run writes track status would wait out the 30 second busy timeout and fail with `database is locked`, leaving the row in `in_progress`. `SqliteDatabase.EnableWalAsync` runs at startup, before the first context is created, and switches the database file to WAL. The mode persists in the file, so later connections read while one writer writes. The connection string keeps a 30 second `Default Timeout`, and a busy write is retried a few times. The retries share one budget (`SqliteDatabase.BusyRetryBudget`), so a database that stays locked costs one budget instead of one budget per attempt.

## Claims and stale rows

A track row in `in_progress` is held by a download that is running. The row survives a crash, so the row alone cannot tell a live download from a dead one: `LiveTrackClaims` holds the claims that are live in this process, and the download pipeline registers a track before it claims the row.

- Startup resets every `in_progress` row to pending. Nothing can be in flight at startup, so they are all stale.
- A new run resets only the claims nobody holds. A single-track download that is running right now keeps its row, and the run leaves that track alone instead of downloading it a second time.
- The coordinator's ETA is paced by the run's own progress, not by the playlist's total download count. A playlist that was mostly downloaded before says nothing about how fast the current run moves.

## Database restore

A database in WAL mode is three files: the database, its `-wal` and its `-shm`. Replacing only the database and leaving the log behind means the next connection replays the old log over the new file: the restored rows are gone and the old ones are back, with `integrity_check` reporting that everything is fine.

The import therefore never touches the live database. `DatabaseBackupService.ImportZipAsync` validates the uploaded zip and stages it next to the live file as `<database>.pending-restore`. The next start applies it: `DatabaseRestore.Apply` runs before the first connection opens, folds the write-ahead log into the live database, removes the sidecars, parks the live file as `<database>.<timestamp>.<id>.preimport.bak` and moves the staged file into place. Work done after the export is not in the imported copy; the parked file keeps it. A staged file without a schema is rejected and the live database stays as it is. A staged import can be discarded before the restart (`DiscardPendingRestoreAsync`); the live database was never touched, so that is the way back.

## Dependency injection

The `MelodyBridge.Application` project provides extension methods for registering services.

### `AddMelodyBridge()`

Registers core services: `DownloadManager`, `SyncEngine`, library scanner, M3U generator, and all infrastructure services including:
- `PlaylistStore`: playlist snapshots, sync modes, `DownloadMissingAsync`, auto-sync due logic
- `DownloaderRegistry`: plugin enable/priority state (DB-persisted)
- `SyncJobRunner`: orchestrates sync jobs (resolve downloaded tracks → M3U / media server)
- `AutoSyncBackgroundService`: syncs playlists whose per-playlist interval has elapsed
- `ScanSchedulingBackgroundService`: scheduled library path scans
- `SyncJobSchedulingBackgroundService`: runs sync jobs whose schedule (preset or cron) is due; jobs only run while the app runs. A never-run job is anchored at `SyncJobs.CreatedAt`, so a new scheduled job first runs at its next fire (a job created while the app was off catches up), and the Sync jobs card shows that next run
- `DataChangeTracker`: a process-wide write stamp; every store write bumps it and the playlist page reloads when it moved, so changes made by another circuit or a background pass reach an open page without polling the database every second
- `SpotifySourceProvider`, `YouTubeSourceProvider`: playlist sources

### `AddMediaServerSyncs()`

Registers every media server sync plugin (Jellyfin, Plex, Subsonic) plus the kind-based directory factory, their settings and user directories, each with its own `HttpClient` via `AddHttpClient`.

### Usage in `Program.cs`

```csharp
builder.Services.AddMelodyBridge();
builder.Services.AddMediaServerSyncs();
```

## Testing

The suite is **NUnit 4** with **Moq** for UI-level DI mocks only.

### Honest-test rules

::: info Why these rules
Each rule exists because a shortcut that looks harmless produces tests
that pass while the feature is broken: in-memory providers hide SQL
behavior, mocks hide what the code actually writes, and shallow
assertions trust cached objects that were never saved.
:::

1. **No InMemory provider for persistence logic**: playlist/store tests use real SQLite files (`UseSqlite("Data Source=...")`), deleted in teardown
2. **Live tests hit the real network**: real open.spotify.com fetches, real yt-dlp downloads (`[Category("Live")]`, CI runs them in a separate job)
3. **Assertions read back from disk or a fresh DbContext**: nothing is asserted from in-memory cached objects
4. **Downloaded files are validated deeply**: the MELODY_ID tag is read from the actual bytes, durations ffprobe-validated

### Running tests

::: code-group
```bash [Fast suite]
# CI default
dotnet test MelodyBridge.sln --filter "FullyQualifiedName!~Tests.Integration"
```

```bash [Live suite]
# needs yt-dlp on PATH + ffprobe; the slskd tests also need
# SLSKD_URL, SLSKD_API_KEY and SLSKD_DOWNLOADS_PATH; the premium tests
# need QOBUZ_TOKEN (or qobuz-dlp's token file) plus QOBUZ_DLP_COMMAND,
# DEEMIX_COMMAND, DEEMIX_DOWNLOADS_PATH and DEEMIX_CLI_JS for deemix,
# and a logged-in tiddl (TIDDL_COMMAND, TIDDL_PATH) for Tidal
# (each group is skipped without its setup)
dotnet test MelodyBridge.sln --filter "Category=PlaylistStore|Category=Live"
```
:::

### Test organization

```
MelodyBridge.Tests/
├── Core/                   # Model and enum tests
├── Infrastructure/        # Scanner, tagger, M3U, DB context tests
│   ├── LibraryScannerTests.cs       # Real tagged MP3s: register + move/update identity
│   ├── M3uGeneratorTests.cs         # Read-back of produced .m3u files
│   ├── JellyfinSyncTests.cs         # Media-server sync clients: Jellyfin,
│   ├── PlexSyncTests.cs             # Plex, Subsonic. Unit tests run against
│   ├── SubsonicSyncTests.cs         # ScriptedHandler, a shared scripted HTTP
│   ├── MediaServerDirectoryTests.cs # boundary; Live tests boot real docker
│   │                                # containers (Category=Live, images must
│   │                                # be pulled; the Plex one reads PLEX_TOKEN)
│   ├── TaglibHelperTests.cs         # Tag reading/writing
│   ├── TaglibMelodyIdFormatsTests.cs # Real ffmpeg files: MP3, FLAC, Opus, M4A roundtrips
│   ├── StatelessRecoveryTests.cs    # Wipe db, re-add, relink, zero redownloads
│   └── DbContextTests.cs
├── Services/
│   ├── PlaylistStoreLiveTests.cs    # Live Spotify fetch, real SQLite
│   ├── PlaylistStoreSyncModeTests.cs # Additive/Mirror with real SQLite
│   ├── MelodyIdsTests.cs           # Deterministic id format table
│   ├── IsrcResolverTests.cs        # Deezer enrichment ranking and caching
│   ├── SpotifySourceProviderTests.cs
│   └── SyncEngineTests.cs
├── ApplicationServices/        # Waterfall behavior
│   ├── DownloadManagerQualityTests.cs # Real MP3s: band gate accepts/rejects
│   ├── DownloadManagerRobustnessTests.cs # Throwing plugins, duration gate,
│   │                                  # non-audio rejection, alternatives
│   ├── SearchRankerTests.cs        # Candidate ranking + duration proximity
│   ├── DownloadCoordinatorTests.cs # Pause/resume/cancel, max concurrency
│   └── QualityRouterTests.cs       # Plugin manifests and routing order
├── Integration/
│   ├── YtDlpDownloaderLiveTests.cs  # Live search/download/tag/ffprobe
│   ├── DownloadMissingAsyncTests.cs # Real plugin writing real tagged files
│   ├── IsrcResolutionTests.cs       # Resolve, pass to the plugin, persist
│   └── SyncJobRunnerTests.cs        # Real .m3u on disk + run history
└── Server/
    ├── UiTests/            # bUnit component tests
    └── SyncControllerTests.cs
```

### Adding new tests

1. Create a new `.cs` file in the appropriate folder under `MelodyBridge.Tests/`
2. Add `[TestFixture]` / `[Test]` attributes; tag live tests with `[Category("Live")]`
3. Follow the honest-test rules above
4. Run with `dotnet test` to verify

## Adding a new downloader plugin

1. Create a class implementing `IDownloader` in `MelodyBridge.Infrastructure/Downloaders/`.
2. Register it: `services.AddSingleton<IDownloader, YourPlugin>();`
3. It appears in the UI (Downloads page) automatically with enable/priority controls.
4. Add tests in `MelodyBridge.Tests/Integration/`.

## Adding a new playlist source

1. Create a class implementing `ISourceProvider` in `MelodyBridge.Infrastructure/Services/`.
2. Register it: `services.AddSingleton<ISourceProvider, YourProvider>();`
3. `PlaylistStore.AddOrRefreshAsync(url)` will route by `CanHandle`.

## Adding a new media server plugin

1. Create a class implementing `IMediaServerSync` in `MelodyBridge.Infrastructure/MediaServers/`, one per `Name`.
2. Register it via `AddMediaServerSyncs()` in `ServiceCollectionExtensions`.
3. Add tests in `MelodyBridge.Tests/Infrastructure/`: unit tests with the shared `ScriptedHandler`, live tests in a `[Category("Live")]` fixture that boots a real container.
