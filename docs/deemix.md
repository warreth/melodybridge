# Deezer and deemix

The Deezer plugin downloads MP3 or FLAC from your own Deezer account
through [deemix](https://github.com/bambanah/deemix). deemix owns the
download and the login (your ARL); MelodyBridge searches Deezer's public
catalogue and hands the track URL over. A paid plan is required for 320 and
FLAC downloads.

The Docker image ships the deemix CLI, so there is nothing to install: paste
your ARL on the Plugins page and it works.

## Setup

1. Log in to [deezer.com](https://www.deezer.com) in your browser.
2. Open the developer tools and find the `arl` cookie for deezer.com
   (Application, then Cookies) and copy its value.
3. On the Plugins page, open the Deezer card's **Settings** row and paste it
   into **Deezer ARL**.

MelodyBridge writes the ARL into deemix's own config before every run, so the
CLI never asks for a login and the setting survives image updates. Without an
ARL the plugin still works when deemix is logged in by other means, for
example through the deemix web UI.

Downloads land in a folder MelodyBridge manages, so there is nothing else to
set up. The command and downloads folder fields under **Advanced** are only
for setups where deemix runs somewhere else.

## How a download runs

The plugin searches Deezer's public API, ranks the results, and runs deemix
for the chosen track into a per-track folder of its own. deemix downloads the
best quality your account allows, up to the requested maximum, and the plugin
moves the file into the playlist folder. Runs are serialized, so parallel
tracks never pick each other's files.

When a track has no file at the requested tier (an older release without
FLAC, or an account without lossless), deemix stops without writing anything
and the plugin reports deemix's own message. The waterfall then moves on to
the next enabled plugin.

## Other setups

The command field takes any deemix invocation, so the plugin also works with a
local build or the official container:

- a local CLI: the path to `deemix`, or `node /path/to/main.cjs`
- the official container: `docker exec Deemix deemix`, with the downloads
  folder set to a path both sides see. That setup needs the Docker socket
  inside the MelodyBridge container, which the shipped compose file does not
  grant.
