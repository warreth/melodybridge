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
   → PlaylistStore (SQLite snapshot, ExternalId identity, sync modes)
   → DownloadMissingAsync → DownloadManager waterfall → YtDlpDownloader
   → MP3 file + MELODY_ID tag + title/artist tags
   → LibraryScanner (reads tags, keeps DB current as files move)
   → SyncJobRunner → M3uGenerator (#EXTINF) or JellyfinSync
```

## Core interfaces

### `IDownloader`: download plugins

```csharp
public interface IDownloader
{
    string Id { get; }
    string Name { get; }
    string Description => string.Empty;
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<DownloaderSearchHit?> SearchAsync(
        string artist, string title, DownloadQuality quality, CancellationToken ct = default);
    Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default);
}
```

Implement this to add a download source. Built-in plugins:

| Plugin | Id | Source | Notes |
|---|---|---|---|
| `LucidaDownloader` | `lucida` | lucida.to (Tidal, Qobuz, Amazon Music) | High quality rips; needs a Cloudflare solver, otherwise skipped |
| `MonochromeDownloader` | `monochrome` | monochrome.tf mirrors (community TIDAL API) | FLAC/AAC rips; tries each instance in order until one answers |
| `DoubleDoubleDownloader` | `doubledouble` | us./eu.doubledouble.top | Submit-and-poll rips of direct track URLs (Tidal, Qobuz, Deezer, Amazon); `SearchAsync` returns null by design, the site search is captcha-gated |
| `SoundCloudDownloader` | `soundcloud` | SoundCloud (via yt-dlp `scsearch`) | Original uploads, often 320 kbps; rejects files under 128 kbps |
| `ArchiveOrgDownloader` | `archiveorg` | Internet Archive (public JSON APIs) | Public-domain and community recordings; rejects files under 128 kbps |
| `YtDlpDownloader` | `ytdlp` | YouTube Music, then YouTube (via yt-dlp) | Widest fallback, best audio as MP3; `ytmsearch1` tried before `ytsearch1` everywhere |

Place new implementations in `MelodyBridge.Infrastructure/Downloaders/` and register via `services.AddSingleton<IDownloader, YourPlugin>()`. Quality-gate downloads against the requested `DownloadQuality` band so bad rips never enter the library.

`DownloadQuality` carries a bitrate band: `MinKbps` and `MaxKbps`, both optional, both hard. Search hits whose measured bitrate falls outside the band are skipped, and downloaded files are probed with `BitrateProbe.MeasureKbps` and rejected+deleted when outside it. Unknown bitrates pass the search gate and leave the verdict to the post-download measurement.

`IDownloaderRegistry` manages the plugin waterfall: enable/disable and priority are persisted per plugin in the `ProviderStates` table. Plugin config values, declared via `IDownloader.ConfigFields` (Key, Label, Placeholder, Description) and read/written through `GetConfigAsync`/`SetConfigAsync`, persist in the `DownloaderSettings` table under `plugin:{id}:{key}` keys and are edited on the Plugins page in an expandable per-plugin panel.

`DownloadCoordinator` runs each playlist with up to `download_max_concurrent` parallel workers (setting, default 2, clamp 1-8, Advanced page). Workers are safe together because `PlaylistStore.DownloadMissingAsync` claims each track with an atomic conditional UPDATE (pending to in_progress) before downloading, so two callers never race on the same track.

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

Implement this to sync playlists to a media server (e.g. Jellyfin). Place implementations in `MelodyBridge.Infrastructure/MediaServers/`.

### `IDownloadManager`: the waterfall

```csharp
public interface IDownloadManager
{
    Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default);
    Task<string?> DownloadTrackAsync(string artist, string title, string outputDirectory, string melodyId, DownloadQuality? quality = null, CancellationToken ct = default);
    IReadOnlyList<DownloadProgress> SnapshotProgress();
}
```

`DownloadTrackAsync` iterates enabled plugins by priority: each searches by artist/title and the first successful download wins. `DownloadAsync` passes a direct URL to the plugins that can handle it.

### Other key types

- **`Playlist`**, **`Track`**, **`TrackQuality`**, **`MediaType`**: Core models in `MelodyBridge.Core/Classes.cs`
- **`PlaylistOutputOptions`**: Output path, relative path toggle, path remap dictionary
- **`TrackEntity`**, **`PlaylistEntity`**, **`ProviderStateRow`**: EF Core entities for persistence
- **`ScanLocationEntity`**, **`SyncJobEntity`**, **`SyncJobRunEntity`**: Library paths and sync job tracking
- **`PlaylistSyncMode`**: `Additive` (removed tracks stay as flagged history) / `Mirror` (local copy matches the source exactly)

## Dependency injection

The `MelodyBridge.Application` project provides extension methods for registering services.

### `AddMelodyBridge()`

Registers core services: `DownloadManager`, `SyncEngine`, library scanner, M3U generator, and all infrastructure services including:
- `PlaylistStore`: playlist snapshots, sync modes, `DownloadMissingAsync`, auto-sync due logic
- `DownloaderRegistry`: plugin enable/priority state (DB-persisted)
- `SyncJobRunner`: orchestrates sync jobs (resolve downloaded tracks → M3U / media server)
- `AutoSyncBackgroundService`: syncs playlists whose per-playlist interval has elapsed
- `ScanSchedulingBackgroundService`: scheduled library path scans
- `SpotifySourceProvider`, `YouTubeSourceProvider`: playlist sources

### `AddJellyfinSync()`

Registers the Jellyfin media server sync plugin with `HttpClient` via `AddHttpClient`.

### Usage in `Program.cs`

```csharp
builder.Services.AddMelodyBridge();
builder.Services.AddJellyfinSync();
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
│   ├── JellyfinSyncTests.cs         # Jellyfin client behavior
│   ├── TaglibHelperTests.cs         # Tag reading/writing
│   └── DbContextTests.cs
├── Services/
│   ├── PlaylistStoreLiveTests.cs    # Live Spotify fetch, real SQLite
│   ├── PlaylistStoreSyncModeTests.cs # Additive/Mirror with real SQLite
│   ├── SpotifySourceProviderTests.cs
│   └── SyncEngineTests.cs
├── Integration/
│   ├── YtDlpDownloaderLiveTests.cs  # Live search/download/tag/ffprobe
│   ├── DownloadMissingAsyncTests.cs # Real plugin writing real tagged files
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

1. Create a class implementing `IMediaServerSync` in `MelodyBridge.Infrastructure/MediaServers/`.
2. Register it via `ServiceCollectionExtensions`.
3. Add tests following the `JellyfinSyncTests` pattern.
