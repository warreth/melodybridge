# Tidal and tiddl

The Tidal plugin downloads lossless and hi-res files from your own Tidal
subscription through [tiddl](https://github.com/oskvr37/tiddl). The tool owns
the login and the download; MelodyBridge's shared Tidal ISRC searcher finds
the track URL and hands it over.

The Docker image ships tiddl, so there is nothing to install: log in once and
it works.

There is also **Tidal (Rhythmax)**, a second Tidal source that needs no Tidal
account. It uses the public Rhythmax hifi API behind tidal.odskyler.com, and
it is the source to use when you do not have a Tidal login. See
[No Tidal account: Rhythmax](#no-tidal-account-rhythmax).

## Setup

1. On the Plugins page, open the Tidal card's **Settings** row and pick the
   FLAC quality: 16-bit / 44.1 kHz (CD quality) or up to 24-bit / 192 kHz
   (hi-res). AAC requests use the 320 or 96 kbps tier.
2. Give the plugin a Tidal session. Either way works and both survive image
   updates, because the session lives on the state mount.
   - **Paste a token**, no terminal needed. Put either a token from your
     account provider or tiddl's whole `auth.json` into **Tidal token**. A
     browser token works too: open tidal.com, find a request to
     `api.tidal.com` in the developer tools, and copy the
     `Authorization: Bearer ...` value. MelodyBridge reads the user id,
     country and expiry from the token and writes tiddl's own auth file,
     so the CLI sees the same login. A token without a refresh token works
     until it expires; the card then says so and a fresh paste fixes it. If
     your provider gives a refresh token, paste it under **Advanced** and
     tiddl renews the session by itself. The user id and country code are
     read from the token when possible; the card says which one is missing
     and the Advanced fields take them.
   - **Or log in with tiddl**: run
     `docker exec -u melodybridge -it melodybridge tiddl auth login`
     (Docker) or `tiddl auth login` from source, and follow the device-code
     prompt. That session carries a refresh token, so it renews itself.

The plugin never owns the Tidal account: it reads the session tiddl stores
in `~/.tiddl/auth.json` (or the folder in `TIDDL_PATH`) and reports itself
unavailable while that session is missing or expired. Running any tiddl
command refreshes the session. In Docker the session lives on the state
mount (`./data/app/home/.tiddl/auth.json` on the host), so it survives image
updates.

## When the card turns red

The card checks the stored session when you save a token and during the
periodic plugin status sweep. An expired or rejected session shows as **token
expired** or **token rejected**, with a red exclamation, a red card border and a
banner at the top of the Plugins page. Paste a fresh token, or run
`tiddl auth login`; saving tests the login immediately.

## How a download runs

The shared Tidal ISRC searcher looks the recording up by ISRC first and falls
back to a title search only when the code is missing or has no result. A hit
carrying the exact code is accepted as that recording even when the title is
spelled differently. The download runs as `tiddl download url` into a private
per-track folder, and the finished file moves into the playlist folder. The
same searcher serves both Tidal sources.

| Request | Tidal tier | Result |
|---|---|---|
| FLAC, quality **CD** | HIGH | 16-bit / 44.1 kHz FLAC |
| FLAC, quality **hi-res** | MAX | Up to 24-bit / 192 kHz FLAC |
| AAC up to 320 kbps | NORMAL | 320 kbps m4a |
| AAC up to 96 kbps | LOW | 96 kbps m4a |

Tidal has no MP3; an MP3 request is never routed here. A request that Tidal
cannot serve in the asked band fails fast and the waterfall moves on instead
of wasting a download.

## No Tidal account: Rhythmax

**Tidal (Rhythmax)** is the no-login Tidal source. It asks the public
Rhythmax hifi API for a Tidal manifest, downloads the DASH segments, and
remuxes the result. No token, no Tidal subscription, and no tiddl install are
needed. The Tidal ISRC searcher supplies the track URL, so this source does
not search Tidal by title itself.

| Request | Rhythmax tier | Result |
|---|---|---|
| FLAC | LOSSLESS | 16-bit / 44.1 kHz FLAC |
| AAC up to 320 kbps | HIGH | 320 kbps AAC in an m4a |
| AAC up to 96 kbps | LOW | 96 kbps AAC in an m4a |
| MP3 or Opus | none | Never routed here |

The playback accounts belong to the public service, not to you. The card can
say **no working Tidal account right now**, **only preview accounts**, or
that Tidal rate limited the request. These are service-side states: retry
later or use the tiddl source with your own account. The shared pool flips
between those states within seconds, so a single red check does not mean the
plugin is broken.

**Tidal country** sets the catalogue region used by the search. **Rhythmax
API base URL** under Advanced is only for a mirror or a local hifi-api.

The default order keeps your own tiddl source first. If tiddl is not logged
in, MelodyBridge skips it and reaches Rhythmax on the same run.

## Other setups

The command field takes any tiddl invocation, so the plugin also works with a
tiddl that runs somewhere else:

- a local install: the path to `tiddl`, or `uvx tiddl`
- another container: `docker exec Tiddl tiddl`, with `TIDDL_PATH` pointing at
  the folder that holds its `auth.json`

The **tiddl app folder** field is only needed when the tool runs with a custom
`TIDDL_PATH`; the plugin passes it through to the CLI as well, so the search
and the download always use the same login.
