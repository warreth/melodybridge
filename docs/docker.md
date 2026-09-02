# Docker deployment

Run MelodyBridge as a self-hosted service using Docker or Podman.

## Quick start

```bash
git clone https://github.com/warreth/melodybridge.git
cd melodybridge
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

This uses [compose.yml](../compose.yml): the published image from `ghcr.io/warreth/melodybridge`, two containers (melodybridge + flaresolverr) on one internal network, the app bound to `127.0.0.1` only, and a `./data/` directory for everything that should survive rebuilds.

## Development

### Development compose

Contributors build from source instead of pulling the image. The dev compose (`docker-compose.yml`) always builds the image from your checkout and runs it with verbose logging and the dev panel (`/dev`):

```bash
docker compose -f docker-compose.yml up -d --build
```

Rerun the same command after code changes: compose rebuilds what changed and restarts. App state lives in `./data/`, so your test library survives rebuilds.

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

See [compose.yml](../compose.yml) for the user setup and [docker-compose.yml](../docker-compose.yml) for the dev defaults.

### Environment variables

Configure everything Jellyfin-related in the web UI (Settings), not through the environment. The only variables you might touch:

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Set to `Development` for dev |
| `ASPNETCORE_URLS` | `http://+:80` | Server binding address |
| `DevPanel__Enabled` | `false` | Enable the /dev testing dashboard |
| `FlareSolverr__Url` | `http://flaresolverr:8191` | Cloudflare solver endpoint (`off` disables Lucida) |

After `docker compose up -d`, open http://localhost:3333 and fill in your Jellyfin base URL, API key and user under Settings. Values are stored in the database volume and apply immediately, no restart needed.

### Volumes

| Host Path | Container Path | Purpose |
|---|---|---|
| `./data/music` | `/music` | Music file storage (point Jellyfin here) |
| `./data/playlists` | `/app/playlists` | Generated playlist output |
| `./data/app` | `/app` | SQLite database and app state |

## Included tools

The Docker image includes `yt-dlp` and `ffmpeg` pre-installed for YouTube and generic URL media downloads.

## Production checklist

::: warning
- Keep the `127.0.0.1` port binding unless you expose the app through a reverse proxy.
- Configure a reverse proxy (nginx, Caddy, Traefik) for TLS.
- Put your Jellyfin API key in the web UI Settings, not in any committed file.
- Never publish the flaresolverr port to the host; the internal network is enough.
:::
