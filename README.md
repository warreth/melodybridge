# MelodyBridge

> Self-hosted music toolbox — download tracks, sync playlists, keep your library organised.

---

## Quick start

```bash
git clone https://github.com/warreth/melodybridge
cd melodybridge
docker compose up -d
# open http://localhost:3333
```

[Configuration →](docs/docker.md)

---

## What it does

- **Download** — tracks and playlists from YouTube, Qobuz, Tidal, SoundCloud, Amazon Music and more via plugin providers with automatic quality fallback (24-bit FLAC → 320 MP3 → …)
- **Tag** — every file gets a unique `MELODY_ID` embedded in its metadata so the library scanner can find it even after moves or renames
- **Scan** — watches your music paths, reads tags (not filenames), keeps the database current
- **Sync** — configurable pipelines: search → tag → M3U playlist → push to Jellyfin (or other media servers)
- **Manage** — Blazor dashboard for accounts, downloads, library, settings, and sync jobs

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
