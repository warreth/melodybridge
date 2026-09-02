# Lucida and FlareSolverr

The Lucida plugin pulls high quality rips from lucida.to (Tidal, Qobuz, Amazon Music and more). Lucida sits behind a Cloudflare challenge, so the plugin needs a Cloudflare solver. MelodyBridge speaks the FlareSolverr protocol (https://github.com/FlareSolverr/FlareSolverr).

## Setup

1. The included `compose.yml` has a `flaresolverr` service. Start it with `docker compose up -d`.
2. In Settings, set the Cloudflare solver URL to `http://flaresolverr:8191` (or `http://127.0.0.1:8191` outside Docker).

::: info
Without a solver, Lucida stays out of the waterfall: nothing breaks, the other plugins just take over.
:::

::: tip
Set the URL to `off` to disable the plugin entirely.
:::

The challenge cookies expire, so the plugin re-solves automatically when Lucida answers with a 403.
