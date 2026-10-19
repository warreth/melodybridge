# Qobuz and qobuz-dlp

The Qobuz plugin downloads lossless and hi-res files from your own Qobuz
subscription through
[qobuz-dlp](https://github.com/infojunkie/qobuz-dlp.py). The tool owns the
download; MelodyBridge searches the official Qobuz API with your token and
hands the track URL over. A paid plan is required: Qobuz does not allow
downloads on free accounts.

The Docker image ships qobuz-dlp, so there is nothing to install: paste your
token on the Plugins page and it works.

## Setup

1. Log in to [play.qobuz.com](https://play.qobuz.com) in your browser.
2. Open the developer tools, watch the Network tab for a request to
   `api.json`, and copy the `X-User-Auth-Token` request header.
3. On the Plugins page, open the Qobuz card's **Settings** row and paste the
   token into **Qobuz token**. Pick the FLAC quality there too: 16-bit /
   44.1 kHz, 24-bit up to 96 kHz, or 24-bit above 96 kHz. MP3 requests always
   use the 320 tier.

MelodyBridge writes the token and the app credentials (scraped from the
Qobuz web player) into qobuz-dlp's config, so the CLI never asks for a
login. The token field can stay empty when qobuz-dlp is logged in elsewhere:
the plugin reads its token file instead.

## When the card turns red

MelodyBridge checks the token when you save it and during the periodic plugin
status sweep. A stale token, a rejected session and a blocked account are
different states, and the card says which one it is. A red exclamation on the
icon and a banner at the top of the Plugins page mean the plugin is skipped.

If the card says **account blocked** (`USER_BLOCKED`), the token itself still
works. Qobuz refuses catalogue and download requests for that account, so a new
token from the same account will not help. Use another account, or ask Qobuz
support.

## How a download runs

The search runs against the official Qobuz API with your token and asks for
the ISRC itself first, which is how a decorated title still finds the exact
recording; without a code it falls back to title, artist and duration. The
download runs as `qobuz-dlp dl` into a hidden per-track folder,
and the finished file moves into the playlist folder. qobuz-dlp falls back
to a lower quality when the requested tier is not available; a FLAC request
that ends up as MP3 is rejected and the waterfall moves on.
