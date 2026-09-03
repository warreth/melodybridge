# Quick start

You need Docker (or Podman) and nothing else.

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

That pulls the published image from GHCR and starts two containers on one
internal network:

- melodybridge on http://localhost:3333, bound to 127.0.0.1 so it is not
  reachable from other machines unless you put a reverse proxy in front
- flaresolverr, the Cloudflare solver used by the Lucida plugin, internal
  only, no published port

The first start creates `./data/` with `music/`, `playlists/` and `app/`
(the SQLite database) so your files survive rebuilds. Point Jellyfin,
Plex or Navidrome at `./data/music`.

## First steps in the UI

1. Open http://localhost:3333 and go to **Playlists**. Paste a public
   Spotify or YouTube playlist link and save it.
2. Open the playlist and press **Download missing**. The plugin waterfall
   fetches each track and tags it.
3. Go to **Sync jobs** and create a job that writes an M3U file or pushes
   the playlist to Jellyfin, Plex or Navidrome.

What that looks like:

<img src="/screens/home.webp" alt="The MelodyBridge dashboard you see after logging in" width="800">

::: tip Jellyfin, Plex or Navidrome on the host
If your media server runs on your host, use
`http://host.docker.internal:8096` (Jellyfin), `:32400` (Plex) or
`:4533` (Navidrome) as the base URL in Settings.
:::

## Running from source

```bash
dotnet run --project MelodyBridge.Server
```

yt-dlp must be on PATH:

```bash
pip3 install --user --break-system-packages yt-dlp
```

The test suite runs with `dotnet test MelodyBridge.sln`. Live tests need
yt-dlp and ffmpeg on PATH.

From here the sidebar covers everything: the [Docker guide](docker.md),
the [User guide](user-guide.md) and [Accounts and OAuth](accounts.md).