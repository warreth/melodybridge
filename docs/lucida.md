# Lucida and FlareSolverr

The Lucida plugin pulls high quality rips from lucida.to, searching Tidal,
Deezer, Amazon Music and Qobuz and taking the best match at the quality the
waterfall asks for. The site sits behind a Cloudflare challenge, so the plugin
needs a solver. MelodyBridge speaks the FlareSolverr protocol
(https://github.com/FlareSolverr/FlareSolverr).

## Setup

Nothing, if you use the shipped compose file: it already starts FlareSolverr
on the same network (service name `flaresolverr`, container name
`mb_flaresolverr`). The solver URL defaults to `auto`, and the app probes both
names on that network.

Outside Docker, or when you run your own solver, set the URL under **Settings,
Network**: `http://127.0.0.1:8191` or wherever yours listens. The options:

- `auto`: look for the container on the Docker network (the default)
- an explicit URL: use that solver
- `off`: disable Lucida entirely

**Test connection** resolves the endpoint and then solves `lucida.to` for real,
not just its `/health` page. A solver whose HTTP port answers but whose browser
cannot finish a challenge is reported as a failure. Without a working solver,
Lucida reports itself unavailable and the waterfall quietly skips it; the other
plugins take over and nothing else breaks.

The app caches a successful clearance for each site and reuses it for later
tracks. It asks for a fresh solve only after a 403, or after the cache ages out.
Auto mode remembers a healthy container and probes again quickly when one is
missing, so a solver that starts a few seconds after the app is picked up by the
next track instead of needing a manual button press.

## Sessions

Cloudflare challenge cookies expire, so when Lucida gets a 403 the plugin asks
the solver for fresh credentials and retries the request once, on its own. The
search uses Qobuz (Netherlands), Amazon, Deezer and Tidal in that order; the
current lucida.to client needs the Qobuz NL storefront and no country for
Amazon. Single-track pages encode the download token with two base64 passes, and
the plugin mirrors that decoding before it submits the load request.
