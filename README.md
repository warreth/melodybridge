# MelodyBridge

> Self-hosted music toolbox: fetch playlists from Spotify or YouTube, download the tracks through a plugin waterfall, and publish clean M3U / Jellyfin playlists.

[![GitHub stars](https://img.shields.io/github/stars/warreth/melodybridge?style=flat-square&logo=github&label=stars)](https://github.com/warreth/melodybridge/stargazers)
[![GitHub forks](https://img.shields.io/github/forks/warreth/melodybridge?style=flat-square&logo=github&label=forks)](https://github.com/warreth/melodybridge/forks)
[![Docker pulls](https://img.shields.io/badge/ghcr.io%2Fwarreth%2Fmelodybridge-docker-blue?style=flat-square&logo=docker)](https://ghcr.io/warreth/melodybridge)
[![License](https://img.shields.io/github/license/warreth/melodybridge?style=flat-square&label=license)](./LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/warreth/melodybridge?style=flat-square&logo=github&label=release)](https://github.com/warreth/melodybridge/releases)

## Screenshots

<!-- SCREENSHOT: dashboard -- paste screenshots/dashboard.png here (dark table, stat cards, connections panel, live playlist progress) -->
![Dashboard](screenshots/dashboard.png)

*The dashboard: stat cards, connection states, recent sync runs and download progress.*

<!-- SCREENSHOT: playlists -- paste screenshots/playlists.png here (playlist cards with cover, track count, last sync) -->
![Playlists](screenshots/playlists.png)

*The Playlists page: saved playlists with their covers and download progress.*

<!-- SCREENSHOT: plugins -- paste screenshots/plugins.png here (waterfall order with up/down arrows and enable toggles) -->
![Plugins](screenshots/plugins.png)

*The Plugins page: reorder the download waterfall, toggle plugins, watch live downloads.*

<!-- SCREENSHOT: playlist-details -- paste screenshots/playlist-details.png here (left settings panel, right live download progress) -->
![Playlist details](screenshots/playlist-details.png)

*Playlist details: per-playlist quality settings on the left, live download progress on the right.*

<!-- SCREENSHOT: settings -- paste screenshots/settings.png here (tabs: Accounts, Jellyfin, Paths, Quality, Network) -->
![Settings](screenshots/settings.png)

*Settings: accounts, Jellyfin, paths, quality checks and the Cloudflare solver.*

## What you get

| Feature | What it does |
|---|---|
| Source fetching | Save public Spotify and YouTube playlists (no API key, no account). With the optional account login (OAuth, read-only scopes) also import private, collaborative and liked playlists. Tracks are stored in SQLite with their platform IDs. |
| Download waterfall | Every track is tried through the enabled plugins in a configurable order: yt-dlp (YouTube Music first, plain YouTube fallback), SoundCloud original uploads, Internet Archive recordings, Lucida (Tidal/Qobuz-quality rips from lucida.to, needs FlareSolverr), Monochrome (community TIDAL rips with mirror fallback), DoubleDouble (multi-service rips from direct URLs). The first plugin that finds and quality-gates a file within your bitrate band wins. Downloads run with a configurable number of parallel workers (Advanced page). |
| Quality control | Per-playlist file format (auto/MP3/FLAC/Opus/AAC) plus an optional bitrate range. A post-download spectrum check measures what a file actually contains - not its header - and flags a "320 kbps" file that is really an up-scaled 128. Lossless webm-to-opus remux keeps files taggable. |
| Library tracking | Every file gets a unique `MELODY_ID` in its ID3v2 TXXX frame plus full tags. Files moved or renamed by other taggers (Beets, Picard) are re-found by tag, not filename. Multiple scan locations, per-location intervals, and startup reconciliation relink or re-queue everything after a restart. |
| Playlist publishing | Sync jobs resolve downloaded tracks into M3U files (with `#EXTINF` metadata) or push playlists through the Jellyfin API with per-user targets. Path and extension remapping handles players that see files under a different path (e.g. inside Docker). |
| Scheduling | Per-playlist auto-sync intervals, and sync jobs scheduled manually, hourly, daily, weekly or by CRON expression. |
| Export and import | Download the whole database as a zip backup, or import one back. Playlists export as JSON, track lists as CSV. |
| Accounts | Spotify and YouTube OAuth with read-only scopes only (`playlist-read-private`, `playlist-read-collaborative`, `user-library-read`, `youtube.readonly`), via the platforms' own libraries. |
| Self-hosted | One container plus an optional FlareSolverr sidecar; yt-dlp and ffmpeg are preinstalled. Docker is the recommended deployment. |

## Quick start (Docker)

You need Docker (or Podman with `podman-compose`) and nothing else.

```bash
git clone https://github.com/warreth/melodybridge
cd melodybridge
docker compose up -d
```

That pulls the published image from GHCR and starts two containers on one internal network:

- **melodybridge** on http://localhost:3333 (bound to 127.0.0.1, so not reachable from other machines without an explicit reverse proxy)
- **flaresolverr**, the Cloudflare solver for the Lucida plugin, internal only (no published port)

It also creates `./data/` with `music/`, `playlists/` and `app/` (the SQLite database) so your files survive rebuilds. Point Jellyfin at `./data/music`.

Podman works the same:

```bash
podman compose up -d --build   # or: podman-compose up -d --build
```

Open http://localhost:3333, go to Settings, and enter your Jellyfin base URL and API key (Jellyfin running on the host is reachable from the container as `http://host.docker.internal:8096`).

The Docker image ships with yt-dlp and ffmpeg preinstalled.

Prefer running from source?

```bash
dotnet run --project MelodyBridge.Server
# yt-dlp needs to be on PATH:
pip3 install --user --break-system-packages yt-dlp
```

[Configuration and volumes →](docs/docker.md)

## Docs

The documentation lives at https://docs.melodybridge.app and builds from the [docs](docs/) folder in this repo. Local preview: `npm run docs:dev`.

| Page | What you'll find |
|---|---|
| [Quick start](docs/quickstart.md) | Five-minute setup with Docker |
| [User guide](docs/user-guide.md) | Walkthrough of every page in the web UI |
| [Features](docs/features.md) | What each feature does and how it works |
| [Docker guide](docs/docker.md) | Compose reference, configuration, volumes, production tips |
| [Accounts and OAuth](docs/accounts.md) | Private playlists and liked songs |
| [Lucida and FlareSolverr](docs/lucida.md) | Optional high quality downloads |
| [Developer guide](docs/developer.md) | Architecture, plugin interfaces, DI, testing |

The desktop wrapper lives in [warreth/melodybridge-desktop](https://github.com/warreth/melodybridge-desktop).

## Optional extras

### Lucida and FlareSolverr

The Lucida plugin pulls high-quality rips from lucida.to (Tidal, Qobuz, Amazon Music and more). Lucida sits behind a Cloudflare challenge, so the plugin needs a Cloudflare solver. MelodyBridge speaks the FlareSolverr protocol (https://github.com/FlareSolverr/FlareSolverr):

1. The included `docker-compose.yml` already has a `flaresolverr` service. Start it with `docker compose up -d`.
2. In the Settings page, set the Cloudflare solver URL to `http://flaresolverr:8191` (or `http://127.0.0.1:8191` outside Docker).
3. Without a solver, Lucida honestly stays out of the waterfall: nothing breaks, the other plugins just take over. Set the URL to `off` to disable the plugin entirely.

The challenge cookies expire, so the plugin re-solves automatically when Lucida answers with a 403.

### Accounts: private playlists and liked songs

MelodyBridge can log into your Spotify and YouTube accounts through the platforms' own OAuth flows and import what only you can see: private playlists, collaborative playlists and your liked songs. Design rules:

- **Read-only scopes only.** Spotify gets `playlist-read-private`, `playlist-read-collaborative` and `user-library-read`; YouTube gets `youtube.readonly`. Nothing that could ever modify your account is requested, so the account cannot be banned for behaviour it dislikes.
- **Official libraries.** Spotify uses SpotifyAPI-NET's PKCE flow, YouTube uses Google's own auth libraries. No logged-in browser cookies are ever scraped.
- **The public fetcher stays independent.** Account and public fetching live in separate providers. When no account is connected, or the account call fails for any reason, the public path (Spotify embed scraping, yt-dlp for YouTube) runs exactly as before.

**Spotify**

1. Create an app at developer.spotify.com (no secret needed).
2. Under Redirect URIs, add the exact URL the page shows: `http://localhost:5085/auth/callback` in dev, or your deployment's `.../auth/callback`.
3. In Settings, paste the Client ID and press Connect Spotify.

Spotify PKCE keeps the refresh token; MelodyBridge stores it in its database and refreshes access tokens automatically. Reconnecting does not break the old login: Spotify keeps it valid.

**YouTube**

1. In Google Cloud Console, enable the YouTube Data API v3 and create OAuth credentials of type Web application.
2. Add the same `.../auth/callback` redirect URI as an authorized redirect URI. For testing outside Google's verification you must add yourself as a test user on the OAuth consent screen.
3. In Settings, paste the client id and secret and press Connect YouTube.

Liked songs arrive through the channel's `likes` playlist (`LL`); private playlists through the normal Data API. Rate limits are generous (quota counts per playlist page).

**Jellyfin favorites** - tracks imported from your liked songs are flagged, and the Jellyfin sync marks them as favorites for the configured user (Settings, Jellyfin panel). With no user configured, the first non-system user is used.

### Desktop wrapper

The optional desktop wrapper moved to its own repo: [warreth/melodybridge-desktop](https://github.com/warreth/melodybridge-desktop). Docker remains the recommended deployment and the one that is continuously tested.

## Architecture

Four layers, dependencies pointing one way:

```
Server (Blazor UI)  →  Application (DownloadManager)  →  Infrastructure (EF Core, providers, yt-dlp)  →  Core (contracts)
```

- `MelodyBridge.Core`: contracts and domain models, zero dependencies
- `MelodyBridge.Application`: orchestration (download waterfall, sync engine)
- `MelodyBridge.Infrastructure`: EF Core SQLite, yt-dlp plugin, source providers, tagging, M3U
- `MelodyBridge.Server`: Blazor Server UI and API controllers

Downloader plugins implement `IDownloader` and are resolved through `IDownloaderRegistry` (enable/disable + priority persisted per plugin). New sources implement `ISourceProvider`. Sync targets implement `IMediaServerSync`.

## Development

The dev compose (`docker-compose.yml`) builds the image from your checkout on every `up`, runs the same stack with verbose logging and the dev panel (`/dev`) enabled:

```bash
docker compose -f docker-compose.yml up -d --build
```

Changed code? Just rerun the command; compose rebuilds what changed.

Without Docker: `dotnet run --project MelodyBridge.Server`.

## Testing philosophy

The test suite is deliberately hard to cheat:

- **Fast suite** (CI, every push): unit + bUnit UI tests, real SQLite files for anything storage-related, no InMemory providers for persistence logic
- **Live suite** (CI, separate job): real network to open.spotify.com, real yt-dlp downloads, assertions read back the actual produced files, their `MELODY_ID` tags, and ffprobe-validated durations
- **Account live tests** skip with instructions (export `MB_SPOTIFY_*` from a real login) instead of faking OAuth
- Every persistence assertion goes through a **fresh DbContext**: nothing is asserted from cached objects

```bash
dotnet test MelodyBridge.sln                                        # fast suite
dotnet test MelodyBridge.sln --filter "Category=PlaylistStore|Category=Live"   # live suite (needs yt-dlp + ffmpeg)
```

## License

AGPL v3 ([LICENSE](LICENSE))
