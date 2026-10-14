# Quick start

MelodyBridge runs in Docker. You need Docker or Podman, nothing else.

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

That starts two containers on one internal network: MelodyBridge on
http://localhost:3333, and FlareSolverr, the Cloudflare solver that the Lucida
plugin needs. The port binds to `127.0.0.1`, so the app is not reachable from
other machines until you put a reverse proxy in front of it.

The first start creates a `./data` folder next to the compose file:

- `./data/music` for downloads
- `./data/playlists` for M3U output
- `./data/app` for the SQLite database

Point Jellyfin, Plex or Navidrome at `./data/music` and they will see everything
MelodyBridge downloads.

## Your first download

1. Open http://localhost:3333. A fresh install shows a three-step checklist that
   ticks itself off as you go. You can skip it and use the dashboard instead.
2. Go to **Playlists**, paste a public Spotify or YouTube playlist link and press
   **Fetch & save**. MelodyBridge stores the track list in its local database.
3. Open the playlist and press **Download missing**. The plugins search and
   download each track, and the list fills in with the real quality of every file.
4. Go to **Sync jobs** and create a job that writes an M3U file or pushes the
   playlist to Jellyfin, Plex or Navidrome. More about that in
   [Publishing playlists](/publishing).

<img src="/screens/playlists.webp" alt="Playlists page with saved playlists and their download progress" width="800">

::: tip Paths inside the container
MelodyBridge only sees the container side of your volumes, so every path you type
in the app is a container path: the music folder is `/music`, not `C:\Music` or
`/home/you/Music`. If your media server runs on the same machine as Docker, use
`http://host.docker.internal:8096` (Jellyfin), `:32400` (Plex) or `:4533`
(Navidrome) as its server URL.
:::

## What next

- [Docker deployment](/docker) for the beta channel, the dev setup, a manual
  build and what every volume and environment variable does.
- [How it works](/concepts) for the download waterfall, the quality checks and
  how your library survives a database wipe.
- [Publishing playlists](/publishing) for the full sync job wizard and schedules.
- [Accounts and imports](/accounts) when you want private playlists or liked
  songs, not just public links.
