# Docker Deployment Guide

This guide covers running MelodyBridge as a self-hosted service using Docker and docker-compose.

---

## Quick Start (docker-compose)

```bash
# Clone the repository
git clone https://github.com/yourusername/melodybridge.git
cd melodybridge

# Start the service
docker compose up -d

# View logs
docker compose logs -f
```

Open [http://localhost:3333](http://localhost:3333).

---

## Manual Docker Build

```bash
docker build -t melodybridge:latest -f Dockerfile .
docker run -d \
  -v ./data/music:/music \
  -v ./data/playlists:/app/playlists \
  -v ./data/keys:/root/.aspnet/DataProtection-Keys \
  -p 3333:80 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e Jellyfin__BaseUrl=http://your-jellyfin:8096 \
  -e Jellyfin__ApiKey=your-api-key \
  --name melodybridge melodybridge:latest
```

---

## docker-compose.yml Reference

The included [docker-compose.yml](../docker-compose.yml) provides a development-ready setup:

```yaml
services:
  melodybridge:
    build: .
    image: melodybridge:local
    container_name: melodybridge_local
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports:
      - "3333:80"
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ASPNETCORE_URLS=http://+:80
      - Jellyfin__BaseUrl=http://host.docker.internal:8096
      - Jellyfin__ApiKey=
      - Logging__LogLevel__Default=Information
    volumes:
      - ./data/keys:/root/.aspnet/DataProtection-Keys
      - ./data/music:/music
      - ./data/playlists:/app/playlists
    restart: unless-stopped
```

### Environment Variables

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Set to `Production` for deployment |
| `ASPNETCORE_URLS` | `http://+:80` | Server binding address |
| `ASPNETCORE_DETAILEDERRORS` | `true` | Detailed error pages (dev only) |
| `Jellyfin__BaseUrl` | `http://host.docker.internal:8096` | Jellyfin server URL |
| `Jellyfin__ApiKey` | *(empty)* | Jellyfin API key |
| `Logging__LogLevel__Default` | `Information` | Default log level |
| `Logging__LogLevel__Microsoft` | `Debug` | ASP.NET framework log level |

### Volumes

| Host Path | Container Path | Purpose |
|---|---|---|
| `./data/music` | `/music` | Music file storage |
| `./data/playlists` | `/app/playlists` | Generated playlist output |
| `./data/keys` | `/root/.aspnet/DataProtection-Keys` | Data protection key persistence |

---

## Path Remapping

If your media server (Jellyfin/Plex) accesses music files at a different path than the MelodyBridge container, use the Path Remapping feature in the Playlist Sync settings.

**Example:** When Jellyfin container sees `/media/music` but MelodyBridge stores at `/music`:

```json
{
  "remap": {
    "/music": "/media/music"
  }
}
```

---

## Production Considerations

1. Set `ASPNETCORE_ENVIRONMENT=Production`
2. Configure a reverse proxy (nginx, Caddy, Traefik) for TLS
3. Use persistent volumes for database and keys
4. Set `Jellyfin__ApiKey` via environment or secrets
5. Remove `ASPNETCORE_DETAILEDERRORS=true` in production
