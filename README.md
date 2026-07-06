# MelodyBridge

Self-hosted music retrieval and playlist sync. Downloads tracks from YouTube and plugin providers, tags them with MELODY_ID, keeps library paths in sync after moves/renames, generates M3U playlists, and pushes them to Jellyfin.

---

## Install

### Docker Compose

```yaml
services:
  melodybridge:
    image: ghcr.io/<your-org>/melodybridge:latest
    container_name: melodybridge
    ports:
      - "3333:80"
    volumes:
      - ./data/music:/music
      - ./data/playlists:/app/playlists
      - ./data/keys:/root/.aspnet/DataProtection-Keys
    environment:
      - Jellyfin__BaseUrl=http://host.docker.internal:8096
    restart: unless-stopped
```

```bash
docker compose up -d
# open http://localhost:3333
```

### GHCR Pull

```bash
docker pull ghcr.io/<your-org>/melodybridge:latest
```

### Build from Source

```bash
git clone https://github.com/<your-org>/melodybridge
cd melodybridge
docker build -t melodybridge .
docker compose up -d
```

---

## Features

- **yt-dlp** — YouTube and generic URL downloads
- **Plugin providers** — Qobuz, Tidal, SoundCloud, Amazon Music (Lucida, SquidWtf, etc.)
- **Quality waterfall** — falls through 24 FLAC → 16 FLAC → 320 MP3 → ... per provider capabilities
- **MELODY_ID tagging** — unique ID embedded in file metadata survives moves/renames
- **Library scanner** — reads tags (not filenames), keeps DB paths current; cron scheduling
- **Playlist sync** — M3U generation with path/extension remapping; Jellyfin push via plugin
- **Source accounts** — YouTube/Spotify playlist sources with auto-sync scheduling
- **Sync jobs** — configurable pipelines (search → tag → playlist → media server)
- **Blazor UI** — web dashboard (Accounts, Downloads, Library, Settings, Sync Jobs)
- **REST API** — all operations exposed via HTTP controllers

---

## Documentation

| Link | What |
|------|------|
| [docs/index.md](docs/index.md) | Full documentation hub |
| [docs/docker.md](docs/docker.md) | Compose reference, env vars, volumes |
| [docs/photino.md](docs/photino.md) | Desktop build & distribution |
| [docs/developer.md](docs/developer.md) | Plugin interfaces, architecture |

### Preview Docs Locally

```bash
npm install
npm run docs:dev
# opens http://localhost:5173
```

Requires Node.js 18+. Uses [VitePress](https://vitepress.dev).

---

## License

AGPL v3. See [LICENSE](LICENSE).
