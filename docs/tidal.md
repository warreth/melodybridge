# Tidal and tiddl

The Tidal plugin downloads lossless and hi-res files from your own Tidal
subscription through [tiddl](https://github.com/oskvr37/tiddl). The tool owns
the login and the download; MelodyBridge searches Tidal's catalogue with that
same session and hands the track URL over.

The Docker image ships tiddl, so there is nothing to install: log in once and
it works.

## Setup

1. On the Plugins page, open the Tidal card's **Settings** row and pick the
   FLAC quality: 16-bit / 44.1 kHz (CD quality) or up to 24-bit / 192 kHz
   (hi-res). AAC requests use the 320 or 96 kbps tier.
2. Log in to tiddl with your Tidal account:
   - Docker: `docker exec -it melodybridge tiddl auth login` and follow the
     device-code prompt.
   - From source: run `tiddl auth login` in a terminal.

The plugin never owns the Tidal account: it reads the session tiddl stores in
`~/.tiddl/auth.json` (or the folder in `TIDDL_PATH`) and reports itself
unavailable while that session is missing or expired. Running any tiddl
command refreshes the session; when it has been idle long enough, run
`tiddl auth login` again.

## How a download runs

The search runs against Tidal's public v1 API with your stored token and picks
the best match on title, artist and duration, with the ISRC as the strongest
signal. The download runs as `tiddl download url` into a private per-track
folder, and the finished file moves into the playlist folder.

| Request | Tidal tier | Result |
|---|---|---|
| FLAC, quality **CD** | HIGH | 16-bit / 44.1 kHz FLAC |
| FLAC, quality **hi-res** | MAX | Up to 24-bit / 192 kHz FLAC |
| AAC up to 320 kbps | NORMAL | 320 kbps m4a |
| AAC up to 96 kbps | LOW | 96 kbps m4a |

Tidal has no MP3; an MP3 request is never routed here. A request that Tidal
cannot serve in the asked band fails fast and the waterfall moves on instead
of wasting a download.

## Other setups

The command field takes any tiddl invocation, so the plugin also works with a
tiddl that runs somewhere else:

- a local install: the path to `tiddl`, or `uvx tiddl`
- another container: `docker exec Tiddl tiddl`, with `TIDDL_PATH` pointing at
  the folder that holds its `auth.json`

The **tiddl app folder** field is only needed when the tool runs with a custom
`TIDDL_PATH`; the plugin passes it through to the CLI as well, so the search
and the download always use the same login.
