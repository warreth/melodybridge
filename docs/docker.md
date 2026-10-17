# Docker deployment

MelodyBridge ships as one app image plus the FlareSolverr companion for the
Lucida plugin. Most people run the published compose file; contributors build
from source instead.

## Quick start

::: code-group
```bash [Docker]
mkdir melodybridge && cd melodybridge
wget https://melodybridge.app/compose.yml
docker compose up -d
```

```bash [Podman]
mkdir melodybridge && cd melodybridge
wget https://melodybridge.app/compose.yml
podman compose up -d
```
:::

The compose file pulls `ghcr.io/warreth/melodybridge:latest` and starts both
containers on one internal network. The app publishes port 3333 on all host
interfaces, so open http://localhost:3333 (or `http://<your-server>:3333` from
another machine). Change the mapping in `compose.yml` to `127.0.0.1:3333:80`
to keep the app reachable from this machine only.

## Volumes and paths

| Host path | Container path | What lives there |
|---|---|---|
| `./data/music` | `/music` | Downloads. Point Jellyfin, Plex or Navidrome here |
| `./data/playlists` | `/app/playlists` | Generated M3U files |
| `./data/app` | `/app/state` | SQLite database and app state |

::: warning Paths inside the app are container paths
MelodyBridge runs inside the container and only sees the container side of these
mappings. Every path you type in the app (Settings defaults, Library locations,
M3U output paths) must look like `/music`, never like the folder on your host.
If a scan finds zero files in a folder that clearly has music, the folder is
probably not mounted, or the host path was used instead. The app repeats this
reminder next to every path input.
:::

## Upgrading from 1.0

Images before v1.1 ran the whole container as root, so everything they
created in `./data` (the database, the M3U files, the downloads) belongs to
root. v1.1 runs the app as UID 1000 and hands the three mount folders
themselves to that user on startup, but it leaves the files inside them
alone, so an upgraded install cannot write its own database until those
files change owner. Stop the stack and fix it once:

```bash
docker compose down
sudo chown -R 1000:1000 ./data
docker compose up -d
```

Fresh installs need nothing: `./data` is still empty. If your own user is
not 1000, the app's files show as UID 1000 on the host from then on; give
the folders to the app user as above, or start the container as your own
user (see Tools in the image).

## Media servers next to it

When Jellyfin, Plex or Navidrome runs as a container on the same compose
network, use its service name as the base URL: `http://jellyfin:8096`,
`http://plex:32400` or `http://navidrome:4533`. A server on the host is reachable
through `http://host.docker.internal:8096` and friends; the shipped compose file
adds the host alias for you.

Either way the music folder must be mounted at the same path on both sides. If
the paths differ, add a path remap rule in the sync job, see
[Publishing playlists](/publishing).

## Beta channel

Prerelease tags (like `v1.0-beta1`) are published under the `:beta` tag, never
`:latest`, so the stable channel stays stable. To run a beta, change the image
line in `compose.yml` from `:latest` to `:beta` and run `docker compose up -d`
again. Going back is the same edit with `:latest`.

Exact version tags stay available forever, so
`ghcr.io/warreth/melodybridge:1.0-beta1` keeps working even after a newer beta
moves the `:beta` tag.

## Environment variables

Almost everything is configured in the web UI and stored in the database. These
are the variables the app reads, with their built-in defaults; the shipped
compose file only sets `ASPNETCORE_URLS`, `FlareSolverr__Url` and
`MELODYBRIDGE_DB`.

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Set to `Development` for the dev setup |
| `ASPNETCORE_URLS` | `http://+:80` | Where the server listens inside the container |
| `DevPanel__Enabled` | `false` | Enable the `/dev` testing dashboard |
| `FlareSolverr__Url` | `auto` | Solver endpoint: `auto`, an explicit URL, or `off` |
| `MELODYBRIDGE_DB` | `/app/state/melodybridge.db` | SQLite database location |

## Manual build

Without compose:

```bash
docker build -t melodybridge:latest .
docker run -d \
  -v ./data/music:/music \
  -v ./data/playlists:/app/playlists \
  -v ./data/app:/app/state \
  -p 3333:80 \
  -e ASPNETCORE_URLS=http://+:80 \
  -e MELODYBRIDGE_DB=/app/state/melodybridge.db \
  --name melodybridge melodybridge:latest
```

Mount state at `/app/state`, never at `/app`: the application itself lives in
`/app`, and mounting over it hides the entrypoint.

## Tools in the image

The image carries everything the download plugins need, so there are no extra
containers to run:

| Tool | Used by | Notes |
|---|---|---|
| ffmpeg + ffprobe | all plugins | Remuxing and duration/bitrate checks |
| yt-dlp | YouTube, SoundCloud | Self-contained binary; the Plugins page can update it in place |
| deemix CLI | Deezer | Runs on Node; the plugin writes your ARL into its config |
| qobuz-dlp | Qobuz | Runs on Python; the plugin writes your token into its config |
| tiddl | Tidal | Runs on Python; log in once with `tiddl auth login` inside the container |

The tools travel with the image: `docker compose pull` brings newer versions,
like the yt-dlp binary does. The image runs on Alpine, which keeps it small:
Debian's ffmpeg alone needed about 490 MB of packages, Alpine's about 130 MB.

The image listens on port 80 by default, so a plain `docker run -p 3333:80`
works without extra environment variables, and it carries a `HEALTHCHECK`
against the home page, so `docker ps` shows whether the app finished starting.

The app itself runs as the unprivileged user `melodybridge` (UID/GID 1000).
On startup the entrypoint hands the three mounted data roots (`/app/state`,
`/app/playlists`, `/music`) to that user and then drops root, so Docker's
root-owned, auto-created mount folders do not block the database or the
downloads. The chown is not recursive: existing files keep their owner (see
Upgrading from 1.0), and everything the app creates is owned by UID 1000.

The entrypoint is the only thing that runs as root, and only for that
handover: the app process itself is never root. To keep even that brief
root step out, start the container as the app user instead:

```yaml
services:
  melodybridge:
    user: "1000:1000"
```

The entrypoint then steps aside and runs the app directly. The data folders
must already be writable by that user before the first start: create them
yourself (`mkdir -p data/music data/playlists data/app`), or chown them once
as in Upgrading from 1.0. On a host where your own user is not 1000, put
your own UID and GID there instead, so the files stay yours.

## Running from source

For development, run the server directly:

```bash
dotnet run --project MelodyBridge.Server
```

yt-dlp must be on PATH. Contributors can also use the dev compose file, which
builds the image from the checkout and runs it with verbose logging and the
`/dev` panel:

```bash
docker compose -f docker-compose.yml up -d --build
```

Build, test and architecture details live in the
[developer guide](/developer) and
[CONTRIBUTING.md](https://github.com/warreth/melodybridge/blob/main/CONTRIBUTING.md).

## Before you expose it

::: warning
- The shipped compose publishes port 3333 on all interfaces. Put a reverse
  proxy in front for TLS, or change the mapping to `127.0.0.1:3333:80` to keep
  the app on this machine only.
- Keep API keys, tokens and passwords in the web UI, never in a committed file.
- Never publish the flaresolverr port to the host; the internal network is
  enough.
:::
