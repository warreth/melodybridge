# Docker Deployment Guide

Run MelodyBridge as a self-hosted service using Docker.

---

## Quick Start

```bash
git clone https://github.com/yourusername/melodybridge.git
cd melodybridge
docker compose up -d
```

Open [http://localhost:3333](http://localhost:3333).

---

## Manual Build

```bash
docker build -t melodybridge:latest .
docker run -d \
  -v ./data/music:/music \
  -v ./data/playlists:/app/playlists \
  -p 3333:80 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e Jellyfin__BaseUrl=http://your-jellyfin:8096 \
  -e Jellyfin__ApiKey=your-api-key \
  --name melodybridge melodybridge:latest
```

---

## Configuration

See [docker-compose.yml](../docker-compose.yml) for the full setup with dev defaults.

### Environment Variables

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Set to `Production` for deployment |
| `ASPNETCORE_URLS` | `http://+:80` | Server binding address |
| `ASPNETCORE_DETAILEDERRORS` | `true` | Detailed error pages (dev only) |
| `Jellyfin__BaseUrl` | `http://host.docker.internal:8096` | Jellyfin server URL |
| `Jellyfin__ApiKey` | *(empty)* | Jellyfin API key |
| `DevPanel__Enabled` | `false` | Enable the /dev testing dashboard |
| `Logging__LogLevel__Default` | `Information` | Default log level |

### Volumes

| Host Path | Container Path | Purpose |
|---|---|---|
| `./data/music` | `/music` | Music file storage |
| `./data/playlists` | `/app/playlists` | Generated playlist output |

---

## Included Tools

The Docker image includes `yt-dlp` and `ffmpeg` pre-installed for YouTube and generic URL media downloads.

---

## Production Checklist

1. Set `ASPNETCORE_ENVIRONMENT=Production`
2. Configure a reverse proxy (nginx, Caddy, Traefik) for TLS
3. Set `Jellyfin__ApiKey` via environment or secrets
4. Remove `ASPNETCORE_DETAILEDERRORS=true` in production
