# MelodyBridge

> Self-hosted music toolbox: fetch playlists from Spotify, download the tracks via yt-dlp, and publish clean M3U / Jellyfin playlists.

---

## Quick start

```bash
git clone https://github.com/warreth/melodybridge
cd melodybridge
docker compose up -d
# open http://localhost:3333
```

The Docker image ships with yt-dlp and ffmpeg preinstalled.

Prefer running from source?

```bash
dotnet run --project MelodyBridge.Server
# yt-dlp needs to be on PATH:
pip3 install --user --break-system-packages yt-dlp
```

[Configuration →](docs/docker.md)

---

## What it does

- **Fetch**: public Spotify playlists, no API key or account needed (embed page scraping), stored in SQLite with per-track platform IDs
- **Download**: a three-plugin waterfall: SoundCloud original uploads (320 kbps-capable), Internet Archive recordings, then YouTube via yt-dlp; every plugin quality-gates its files (no low-bitrate rips)
- **Tag**: every file gets a unique `MELODY_ID` written into its ID3v2 TXXX frame so the library scanner can find it even after moves or renames
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
