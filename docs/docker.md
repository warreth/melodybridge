# Docker deployment

Run MelodyBridge as a self-hosted service using Docker or Podman.

## Quick start

```bash
mkdir melodybridge && cd melodybridge
wget https://melodybridge.app/compose.yml
```

::: code-group
```bash [Docker]
docker compose up -d
```
```bash [Podman]
podman compose up -d   # or: podman-compose up -d
```
:::

Open http://localhost:3333.

This pulls the published image from `ghcr.io/warreth/melodybridge` via [compose.yml](https://melodybridge.app/compose.yml): two containers (melodybridge + flaresolverr) on one internal network, the app bound to `127.0.0.1` only, and a `./data/` directory for everything that should survive rebuilds.

## Development

### Development compose

Contributors build from source instead of pulling the image: that is the one flow that needs a clone:

```bash
git clone https://github.com/warreth/melodybridge.git
cd melodybridge
docker compose -f docker-compose.yml up -d --build
```

The dev compose (`docker-compose.yml`) always builds the image from your checkout and runs it with verbose logging and the dev panel (`/dev`). Rerun the same command after code changes: compose rebuilds what changed and restarts. App state lives in `./data/`, so your test library survives rebuilds.

### Running from source

```bash
dotnet run --project MelodyBridge.Server
```

The test suite: `dotnet test MelodyBridge.sln`. Live tests need yt-dlp and ffmpeg on PATH.

## Manual build

```bash
docker build -t melodybridge:latest .
docker run -d \
  -v ./data/music:/music \
  -v ./data/playlists:/app/playlists \
  -v ./data/app:/app \
  -p 127.0.0.1:3333:80 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  --name melodybridge melodybridge:latest
```

## Configuration

See the [user compose file](https://melodybridge.app/compose.yml) for the published-image setup and the dev compose file in the repo ([docker-compose.yml](https://github.com/warreth/melodybridge/blob/main/docker-compose.yml)) for the dev defaults.

### Environment variables

Configure everything media-server-related (Jellyfin, Plex, Navidrome) in the web UI (Settings), not through the environment. The only variables you might touch:

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Set to `Development` for dev |
| `ASPNETCORE_URLS` | `http://+:80` | Server binding address |
| `DevPanel__Enabled` | `false` | Enable the /dev testing dashboard |
| `FlareSolverr__Url` | `auto` | Cloudflare solver endpoint: `auto` detects the container on the compose network, an explicit URL uses that one, `off` disables Lucida |

After `docker compose up -d`, open http://localhost:3333 and fill in your connection under Settings: Jellyfin takes a base URL, API key and user; Plex takes a base URL and X-Plex-Token; Navidrome takes a base URL, username and password. Values are stored in the database volume and apply immediately, no restart needed.

### Volumes

| Host Path | Container Path | Purpose |
|---|---|---|
| `./data/music` | `/music` | Music file storage (point Jellyfin, Plex or Navidrome here) |
| `./data/playlists` | `/app/playlists` | Generated playlist output |
| `./data/app` | `/app` | SQLite database and app state |

### Media servers in Docker

When your media server runs as another container on the same compose
network, use its service name as the base URL: `http://jellyfin:8096`,
`http://plex:32400` or `http://navidrome:4533`. The music folder must be
mounted at the same path on both sides; if it differs, add a path remap
rule in the sync job. A server on the host is reached through
`http://host.docker.internal` instead.

## Included tools

The Docker image includes `yt-dlp` and `ffmpeg` pre-installed for YouTube and generic URL media downloads.

## Production checklist

::: warning
- Keep the `127.0.0.1` port binding unless you expose the app through a reverse proxy.
- Configure a reverse proxy (nginx, Caddy, Traefik) for TLS.
- Put your Jellyfin API key, Plex token or Navidrome password in the web UI Settings, not in any committed file.
- Never publish the flaresolverr port to the host; the internal network is enough.
:::
