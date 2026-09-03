# Lucida and FlareSolverr

The Lucida plugin pulls high quality rips from lucida.to. It searches several services in turn, Tidal, Deezer, Amazon Music and Qobuz, and rips the best match at the quality the waterfall asks for. Lucida sits behind a Cloudflare challenge, so the plugin needs a Cloudflare solver. MelodyBridge speaks the FlareSolverr protocol (https://github.com/FlareSolverr/FlareSolverr).

## Setup

1. The included `compose.yml` has a `flaresolverr` service on the same network as the app. Start it with `docker compose up -d`.
2. Nothing else: the solver URL defaults to `auto`, and the app detects the FlareSolverr container by its Docker DNS name. Outside Docker, or with a custom solver, set the URL in Settings to `http://127.0.0.1:8191` or your own address.

::: info
Without a solver, Lucida stays out of the waterfall: nothing breaks, the other plugins just take over.
:::

::: tip
Set the URL to `off` to disable the plugin entirely, or `auto` to detect the container again.
:::

The challenge cookies expire, so the plugin re-solves automatically when Lucida answers with a 403.
