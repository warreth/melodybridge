# Lucida and FlareSolverr

The Lucida plugin pulls high quality rips from lucida.to, searching Tidal,
Deezer, Amazon Music and Qobuz and taking the best match at the quality the
waterfall asks for. The site sits behind a Cloudflare challenge, so the plugin
needs a solver. MelodyBridge speaks the FlareSolverr protocol
(https://github.com/FlareSolverr/FlareSolverr).

## Setup

Nothing, if you use the shipped compose file: it already starts a `flaresolverr`
container on the same network. The solver URL defaults to `auto`, and the app
finds the container by its Docker DNS name.

Outside Docker, or when you run your own solver, set the URL under **Settings,
Network**: `http://127.0.0.1:8191` or wherever yours listens. The options:

- `auto`: look for the container on the Docker network (the default)
- an explicit URL: use that solver
- `off`: disable Lucida entirely

**Test connection** tells you what it found. Without a working solver, Lucida
reports itself unavailable and the waterfall quietly skips it; the other plugins
take over and nothing else breaks.

## Sessions

Cloudflare challenge cookies expire, so when Lucida gets a 403 the plugin asks
the solver for fresh credentials and retries the request once, on its own.
