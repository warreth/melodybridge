# MelodyBridge docs

MelodyBridge is a self-hosted music toolbox. It saves the tracks of a Spotify
or YouTube playlist, downloads the music through a waterfall of plugins,
and publishes the result as an M3U file or a Jellyfin playlist.

## Start here

- [Quick start](quickstart.md): get MelodyBridge running in five minutes
  with Docker
- [Features](features.md): what the app does and how each part works
- [User guide](user-guide.md): a walkthrough of every page in the web UI

## Deploy and configure

- [Docker guide](docker.md): compose reference, volumes, environment
  variables, production tips
- [Accounts and OAuth](accounts.md): connect Spotify and YouTube to import
  private playlists and liked songs
- [Lucida and FlareSolverr](lucida.md): optional high quality downloads from
  Tidal and Qobuz sources

## Contribute

- [Developer guide](developer.md): architecture, plugin interfaces, testing
- [Photino desktop build](photino.md): optional desktop wrapper

## About this site

This site is built with [VitePress](https://vitepress.dev). To preview it
locally you need Node.js 18 or newer:

```bash
npm install
npm run docs:dev
```

The dev server opens on http://localhost:5173. The production build runs in
CI on every push to main that touches the docs.
