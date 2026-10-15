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

### The data flow

```
Spotify playlist URL
   → SpotifySourceProvider (embed page scraping, no API key)
   → PlaylistStore (SQLite snapshot, deterministic MelodyIds, sync modes)
   → DownloadMissingAsync → IsrcResolver (Deezer) → DownloadManager waterfall
   → MP3 file + title/artist tags + MELODY_ID written by the manager
   → LibraryScanner (reads tags, keeps DB current as files move)
   → SyncJobRunner → M3uGenerator (#EXTINF) or IMediaServerSync (Jellyfin, Plex, Navidrome)

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

Plugins whose catalog indexes ISRCs (the Qobuz-backed ones) search by
`query.Isrc` first when it is present and fall back to the text query
when that yields no confident hit. YouTube, SoundCloud, Archive.org and
the TIDAL proxies do not index ISRCs and ignore the field (the
HiFi-RestAPI behind Monochrome exposes only s/a/al/v/p search
parameters, verified against its OpenAPI spec).

`PluginCapabilities` is the plugin's honest manifest: every quality level it
can deliver (container, the bitrate band it arrives in, whether the content is
really lossless) and, per level, whether the plugin can be *asked* for it or
merely hands back what the service decides. It has no default implementation,
so a plugin that does not declare its limits does not compile. The router
filters on this data before any network call and orders the survivors: with
**No filter** the user's Plugins-page order is the order, and with a
constrained request the plugins that control the quality run before the ones
that only report what may arrive. No plugin name, no priority table and no
magic band is involved. A non-strict playlist additionally walks
`QualityLadder.Build`: the request first, then the best declared level per
other container, stepping down until a source delivers (`DownloadManager`
takes a `DownloadFallback` policy for this); the request's cap is never
exceeded.

Plugins do not write the MELODY_ID tag themselves. `DownloadManager`
writes it once for every plugin after the file passes the quality band;
a plugin's tagging code only handles its own metadata.

Implement this to add a download source. Built-in plugins:

| Plugin | Id | Source | Notes |
|---|---|---|---|
| `LucidaDownloader` | `lucida` | lucida.to (Tidal, Qobuz, Amazon Music) | High quality rips; needs a Cloudflare solver, otherwise skipped; Qobuz/Amazon are searched by ISRC first; the load API has no quality parameter, so its manifest is honest-but-uncontrollable |
| `MonochromeDownloader` | `monochrome` | monochrome.tf mirrors (community TIDAL API) | FLAC/AAC rips; tries each instance in order until one answers; the manifest names the real tiers (AAC 96/320, FLAC lossless), not a continuous band; the proxy has no ISRC filter |
| `DoubleDoubleDownloader` | `doubledouble` | us./eu.doubledouble.top | Submit-and-poll rips of direct track URLs (Tidal, Qobuz, Deezer, Amazon); `SearchAsync` returns null by design, the site search is captcha-gated; no quality parameter, so its manifest is uncontrollable too |
| `SoundCloudDownloader` | `soundcloud` | SoundCloud (via yt-dlp `scsearch`) | Original uploads, often 320 kbps; top-5 ranked; rejects files under 128 kbps |
| `ArchiveOrgDownloader` | `archiveorg` | Internet Archive (public JSON APIs) | Public-domain and community recordings; picks the matching file inside a multi-track item, not the first one; rejects files under 128 kbps |
| `DabMusicDownloader` | `dabmusic` | dabmusic.xyz (community Qobuz rips) | FLAC up to 24/192 and MP3 320, no account needed; searches by ISRC first; community service, regularly offline, so availability is probed and cached before use |
| `YtDlpDownloader` | `ytdlp` | YouTube (via yt-dlp) | Widest fallback; audio-only best stream, top-5 ranked by match and duration; explicit formats transcode to the requested cap |

Place new implementations in `MelodyBridge.Infrastructure/Downloaders/` and register via `services.AddSingleton<IDownloader, YourPlugin>()`. Quality-gate downloads against the requested `DownloadQuality` band so bad rips never enter the library.

Every plugin-produced file is sniffed with `AudioSignature` (magic bytes, never the extension) before `DownloadManager` accepts it: an HTML error page or JSON body served under a 200 status is deleted and the waterfall moves on. Plugins that save files themselves delete partial output on failure and cancellation; the yt-dlp-backed plugins keep yt-dlp's resumable `.part` fragments (never final output, and the next attempt resumes from them).

`DownloadQuality` carries a bitrate band: `MinKbps` and `MaxKbps`, both optional, both hard. Search hits whose measured bitrate falls outside the band are skipped, and downloaded files are probed with `BitrateProbe.MeasureKbps` and rejected+deleted when outside it. Unknown bitrates pass the search gate and leave the verdict to the post-download measurement.

`IDownloaderRegistry` manages the plugin waterfall: enable/disable and priority are persisted per plugin in the `ProviderStates` table. Plugin config values, declared via `IDownloader.ConfigFields` (Key, Label, Placeholder, Description) and read/written through `GetConfigAsync`/`SetConfigAsync`, persist in the `DownloaderSettings` table under `plugin:{id}:{key}` keys and are edited on the Plugins page in an expandable per-plugin panel.

`DownloadCoordinator` runs each playlist's downloads in parallel workers and is observable through `Snapshot()`; see `IDownloadManager` below for the per-run work-list semantics.

### `ISourceProvider`: playlist sources

```csharp
public interface ISourceProvider
{
    string Name { get; }
    Platform Platform { get; }
    bool CanHandle(string sourceIdentifier);
    Task<Playlist> GetPlaylistAsync(string sourceIdentifier);
    Task<string?> ResolveTrackUrlAsync(string query);
}
```

Implement this to support a new playlist platform. `PlaylistStore` picks the provider whose `CanHandle` accepts the URL.

### `IMediaServerSync`: media server targets

```csharp
public interface IMediaServerSync
{
    string Name { get; }
    Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default);
}
```

Implement this to sync playlists to a media server. Built-in targets are resolved by `Name` (`"Jellyfin"`, `"Plex"`, `"Navidrome"`): place implementations in `MelodyBridge.Infrastructure/MediaServers/`: `JellyfinSync.cs`, `PlexSync.cs`, `NavidromeSync.cs`. Each has a settings pair (`JellyfinSettings.cs`, `PlexSettings.cs`, `NavidromeSettings.cs`) reading connection values from the settings table, and `IMediaServerDirectory` (`JellyfinUserDirectory.cs`, `PlexNavidromeDirectories.cs`) supplies the wizard's Test connection and user picker. Per-job connection overrides travel in `PlaylistOutputOptions.MediaServerConnection` (base URL, credential, optional user id).

### `IDownloadManager`: the waterfall

```csharp
public interface IDownloadManager
{
    Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default);
    Task<string?> DownloadTrackAsync(TrackQuery query, string outputDirectory, string melodyId, CancellationToken ct = default);
    IReadOnlyList<DownloadProgress> SnapshotProgress();
    string? LastFailure(string melodyId);
}
```

`DownloadTrackAsync` routes the enabled plugins through `QualityRouter` first: incompatible plugins are excluded before any network call, survivors are ranked for the request, and the first successful download wins. Without a fallback policy the request is strict: exactly the requested quality or nothing. With a `DownloadFallback` (a non-strict playlist) a miss walks the `QualityLadder`, stepping down through the levels the enabled plugins declare. Every plugin call is individually guarded (a plugin that throws on availability, search or download is skipped, never fatal), each hit is duration-checked before any bytes are spent, and each produced file must sniff as real audio. When every plugin is excluded the track fails fast with "your quality filters excluded every plugin". On success the manager writes the MELODY_ID tag. `DownloadAsync` passes a direct URL to the plugins that can handle it.

## ISRC: exact matching

`Track.Isrc` and `Tracks.Isrc` carry the International Standard
Recording Code when it is known. Two sources fill it:

1. **The Spotify account path** batches `GET /v1/tracks?ids=` (50 ids
   per call) after fetching playlist items or liked songs, because
   playlist items carry simplified tracks without `external_ids`. A
   failed batch just leaves those tracks without an ISRC.
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
when the source does not provide one. The Qobuz-backed plugins search
by ISRC first (see above); nothing else changes for the other plugins.

Spotify's public web endpoints were checked and do not expose ISRCs:
the embed page and the track page carry none, `get_access_token` is
blocked (403), and the partner GraphQL API needs an internal client
token and rotating persisted-query hashes. YouTube Music exposes no
ISRC either (yt-dlp's info JSON has no such field), so Deezer is the
enrichment source of record.

`DownloadCoordinator` runs each playlist with up to `download_max_concurrent` parallel workers (setting, default 2, clamp 1-8, Advanced page). Each run snapshots its work list (pending/failed tracks, Position order) and every track is attempted exactly once per run: a permanently failing track can no longer spin the run forever, and the visible queue plus ETA count the work still in flight. The store's claim (`DownloadTrackAsync(int)`) is still atomic, so a single-track click and a running batch never download the same track twice. Stale `in_progress` claims (crash, hard shutdown) are reset to pending at startup.

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
passes the quality band, so no rejected file is ever tagged.

Legacy databases keep their `mb-{guid}` rows: those tags
still match their files. A SchemaPatcher backfill fills the
deterministic id only for rows that have an ExternalId and no MelodyId.

## Hardlink deduplication

The identity tag also powers deduplication. Before any download,
`PlaylistStore` looks for another playlist's row with the same MelodyId
and a downloaded file, and compares that file's measured quality with
the requested band:

- Exact match: `HardLinkService.Create` (P/Invoke `link(2)` on Unix,
  `CreateHardLinkW` on Windows; `File.CreateHardLink` arrives in
  .NET 11) places a second name for the same bytes into this
  playlist's folder. `IsHardLink` marks the row, no network call runs.
- Quality difference: the row pauses as `needs-review` with a warning;
  the UI offers "Download new version" and "Hardlink existing version"
  (`ResolveReviewAsync`). The store never picks for the user. "Download
  new" records a one-shot `Tracks.SkipDedup` flag, so the next attempt
  skips the dedup check once and really fetches a fresh file instead of
  pausing on the same candidate again.
- Cross-device failure (EXDEV): the exception carries the honest
  message, the store logs it and the track downloads normally. A
  `File.Copy` fallback is deliberately forbidden.

Deletion is name-wise by hard link semantics: removing one playlist's
row removes that playlist's file name only, and both `RemoveTrackAsync`
and `DeleteAsync` additionally keep any path another row still
references. No central pool exists; playlist folders stay standard.

The setting `dedup_enabled` (default on) toggles the whole feature from
the Advanced page. `Tracks.MelodyId` carries a plain (non-unique) index:
the same source track legitimately lives in several playlists, and a
SchemaPatcher step migrates older databases that had the unique one.

## Dependency injection

The `MelodyBridge.Application` project provides extension methods for registering services.

### `AddMelodyBridge()`

Registers core services: `DownloadManager`, `SyncEngine`, library scanner, M3U generator, and all infrastructure services including:
- `PlaylistStore`: playlist snapshots, sync modes, `DownloadMissingAsync`, auto-sync due logic
- `DownloaderRegistry`: plugin enable/priority state (DB-persisted)
- `SyncJobRunner`: orchestrates sync jobs (resolve downloaded tracks → M3U / media server)
- `AutoSyncBackgroundService`: syncs playlists whose per-playlist interval has elapsed
- `ScanSchedulingBackgroundService`: scheduled library path scans
- `SyncJobSchedulingBackgroundService`: runs sync jobs whose schedule (preset or cron) is due; jobs only run while the app runs
- `SpotifySourceProvider`, `YouTubeSourceProvider`: playlist sources

### `AddMediaServerSyncs()`

Registers every media server sync plugin (Jellyfin, Plex, Navidrome), their settings and user directories, each with its own `HttpClient` via `AddHttpClient`.

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
# needs yt-dlp on PATH + ffprobe
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
│   ├── PlexSyncTests.cs             # Plex, Navidrome. Unit tests run against
│   ├── NavidromeSyncTests.cs        # ScriptedHandler, a shared scripted HTTP
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
