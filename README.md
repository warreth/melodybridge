# MelodyBridge

> Self-hosted music toolbox: fetch playlists from Spotify, download the tracks via yt-dlp, and publish clean M3U / Jellyfin playlists.

---

## Quick start

You need Docker (or Podman with `podman-compose`) and nothing else.

```bash
git clone https://github.com/warreth/melodybridge
cd melodybridge
docker compose up -d
```

That pulls the published image from GHCR and starts two containers on one
internal network:

- **melodybridge** on http://localhost:3333 (bound to 127.0.0.1, so not
  reachable from other machines without an explicit reverse proxy)
- **flaresolverr**, the Cloudflare solver for the Lucida plugin, internal
  only (no published port)

It also creates `./data/` with `music/`, `playlists/` and `app/` (the
SQLite database) so your files survive rebuilds. Point Jellyfin at
`./data/music`.

Podman works the same:

```bash
podman compose up -d --build   # or: podman-compose up -d --build
```

Open http://localhost:3333, go to Settings, and enter your Jellyfin base
URL and API key (Jellyfin running on the host is reachable from the
container as `http://host.docker.internal:8096`).

The Docker image ships with yt-dlp and ffmpeg preinstalled.

Prefer running from source?

```bash
dotnet run --project MelodyBridge.Server
# yt-dlp needs to be on PATH:
pip3 install --user --break-system-packages yt-dlp
```

[Configuration →](docs/docker.md)

---

## Development

The dev compose (`docker-compose.yml`) builds the image from your
checkout on every `up`, runs the same stack with verbose logging and the
dev panel (`/dev`) enabled:

```bash
docker compose -f docker-compose.yml up -d --build
```

Changed code? Just rerun the command; compose rebuilds what changed.
Run the test suite with `dotnet test MelodyBridge.sln`. Live tests need
yt-dlp and ffmpeg on PATH.

Without Docker: `dotnet run --project MelodyBridge.Server`.

---

## Lucida and FlareSolverr (optional)

The Lucida plugin pulls high-quality rips from lucida.to (Tidal, Qobuz,
Amazon Music and more). Lucida sits behind a Cloudflare challenge, so the
plugin needs a Cloudflare solver. MelodyBridge speaks the FlareSolverr
protocol (https://github.com/FlareSolverr/FlareSolverr):

1. The included `docker-compose.yml` already has a `flaresolverr` service.
   Start it with `docker compose up -d`.
2. In the Settings page, set the Cloudflare solver URL to
   `http://flaresolverr:8191` (or `http://127.0.0.1:8191` outside Docker).
3. Without a solver, Lucida honestly stays out of the waterfall: nothing
   breaks, the other plugins just take over.

The challenge cookies expire, so the plugin re-solves automatically when
Lucida answers with a 403.

## Accounts: private playlists and liked songs (optional)

MelodyBridge can log into your Spotify and YouTube accounts through the
platforms' own OAuth flows and import what only you can see: private
playlists, collaborative playlists and your liked songs. Design rules:

- **Read-only scopes only.** Spotify gets `playlist-read-private`,
  `playlist-read-collaborative` and `user-library-read`; YouTube gets
  `youtube.readonly`. Nothing that could ever modify your account is
  requested, so the account cannot be banned for behaviour it dislikes.
- **Official libraries.** Spotify uses SpotifyAPI-NET's PKCE flow, YouTube
  uses Google's own auth libraries. No logged-in browser cookies are ever
  scraped.
- **The public fetcher stays independent.** Account and public fetching
  live in separate providers. When no account is connected, or the
  account call fails for any reason, the public path (Spotify embed
  scraping, yt-dlp for YouTube) runs exactly as before.

### Spotify

1. Create an app at developer.spotify.com (no secret needed).
2. Under Redirect URIs, add the exact URL the page shows:
   `http://localhost:5085/auth/callback` in dev, or your deployment's
   `.../auth/callback`.
3. In Settings, paste the Client ID and press Connect Spotify.

Spotify PKCE keeps the refresh token; MelodyBridge stores it in its
database and refreshes access tokens automatically. Reconnecting does not
break the old login: Spotify keeps it valid.

### YouTube

1. In Google Cloud Console, enable the YouTube Data API v3 and create
   OAuth credentials of type Web application.
2. Add the same `.../auth/callback` redirect URI as an authorized redirect
   URI. For testing outside Google's verification you must add yourself
   as a test user on the OAuth consent screen.
3. In Settings, paste the client id and secret and press Connect YouTube.

Liked songs arrive through the channel's `likes` playlist (`LL`); private
playlists through the normal Data API. Rate limits are generous (quota
counts per playlist page).

### Jellyfin favorites

Tracks imported from your liked songs are flagged, and the Jellyfin sync
marks them as favorites for the configured user (Settings, Jellyfin
panel). With no user configured, the first non-system user is used.

## What it does

- **Fetch**: public Spotify playlists, no API key or account needed (embed page scraping), stored in SQLite with per-track platform IDs
- **Download**: a four-plugin waterfall: Lucida (Tidal/Qobuz-quality rips, optional), SoundCloud original uploads (320 kbps-capable), Internet Archive recordings, then YouTube via yt-dlp; every plugin quality-gates its files (no low-bitrate rips)
- **Quality control**: per-playlist file format (auto/MP3/FLAC/Opus/AAC) plus an optional bitrate range, and a post-download spectrum check that measures what a file really contains and warns you when a "320 kbps" file is really an up-scaled 128
- **Tag**: every file gets a unique `MELODY_ID` written into its ID3v2 TXXX frame, plus full tags (title, artist, album, track number) written from the playlist data so players show the right names
- **Sync modes**: per playlist: *Additive* keeps removed tracks as flagged history, *Mirror* makes the local copy exactly match the source; per-playlist auto-sync intervals
- **Publish**: sync jobs resolve downloaded tracks into standard M3U files (with `#EXTINF` metadata) or push playlists to Jellyfin
- **Scan**: watches your music paths, reads tags (not filenames), keeps the database current
- **Manage**: Blazor dashboard for playlists, downloads, plugin priority, library, and sync jobs

---

## Architecture

Four layers, dependencies pointing one way:

```
Server (Blazor UI)  →  Application (DownloadManager)  →  Infrastructure (EF Core, providers, yt-dlp)  →  Core (contracts)
```

- `MelodyBridge.Core`: contracts and domain models, zero dependencies
- `MelodyBridge.Application`: orchestration (download waterfall, sync engine)
- `MelodyBridge.Infrastructure`: EF Core SQLite, yt-dlp plugin, source providers, tagging, M3U
- `MelodyBridge.Server`: Blazor Server UI and API controllers

Downloader plugins implement `IDownloader` and are resolved through `IDownloaderRegistry` (enable/disable + priority persisted per plugin). New sources implement `ISourceProvider`.

---

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

---

## Docs

| Link | What you'll find |
|---|---|
| [Docker guide](docs/docker.md) | Compose reference, env vars, volumes, production tips |
| [Developer guide](docs/developer.md) | Architecture, plugin interfaces, DI, testing |
| [Photino desktop build](docs/photino.md) | Optional desktop wrapper |

---

## License

AGPL v3
