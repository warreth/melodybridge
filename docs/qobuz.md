# Qobuz and qobuz-dlp

The Qobuz plugin downloads lossless and hi-res files from your own Qobuz
subscription. It does not talk to Qobuz itself: it drives
[qobuz-dlp](https://github.com/infojunkie/qobuz-dlp.py), which owns the
login and the download, and searches through the official Qobuz API with
your token. A paid plan is required; Qobuz does not allow downloads on free
accounts.

## Setup

1. Install qobuz-dlp: `pipx install qobuz-dlp` (or `pip install qobuz-dlp`).
2. Run `qobuz-dlp` once and log in. The tool keeps its config and token in
   `~/.config/qobuz-dl` (`%APPDATA%\qobuz-dl` on Windows).
3. On the Plugins page, open the Qobuz card's **Settings** panel. The
   command defaults to `qobuz-dlp`; set a full path when it is not on PATH.
   The token field can stay empty: MelodyBridge reads qobuz-dlp's own token
   file, so a logged-in tool just works.
4. Pick the FLAC quality: 6 for 16/44, 7 for 24-bit up to 96 kHz, 27 for
   24-bit above 96 kHz. MP3 requests always use the 320 tier.

The plugin reports itself unavailable until the command and a valid token
are both found, so a half-finished setup never costs waterfall time.

## How a download runs

The search runs against the official Qobuz API with your token and picks
the best match on title, artist and duration, with the ISRC as the strongest
signal. The download runs as `qobuz-dlp dl` into a hidden per-track folder,
and the finished file moves into the playlist folder. qobuz-dlp falls back
to a lower quality when the requested tier is not available; a FLAC request
that ends up as MP3 is rejected and the waterfall moves on.
