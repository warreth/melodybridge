# Jumo (Qobuz rips)

The Jumo plugin pulls tracks from jumo-dl.pages.dev, a community front-end for
Qobuz. It needs no account: the service holds the storefront access, the plugin
only talks to its API.

Search results and stream descriptors come from that API. The audio itself comes
straight from the signed Qobuz CDN links the descriptor carries, and MelodyBridge
decrypts it locally. No extra tools are involved: no CLI, no account, no ffmpeg.

## Quality

The plugin maps the requested quality onto three Qobuz tiers:

| Request | What arrives |
|---|---|
| FLAC (any) | The tier set in the plugin settings: Original (24-bit, up to 192 kHz), 24-bit up to 96 kHz, or CD quality (16-bit, 44.1 kHz) |
| MP3 320 | MP3 at 320 kbps |
| No filter | The lossless tier from the settings |

Lossless downloads land as native FLAC files, MP3 downloads as plain MP3. The
waterfall's quality check still measures the real bitrate after the download, and
rejects a file that does not match the request.

## Region

Qobuz catalogs differ per storefront. The **Region** setting takes a two letter
code (`US`, `GB`, `FR`, `DE`, `CA`, `JP` and so on). Leave it empty to use the
service's current default.

The service offers a handful of regions at a time; the plugin reads that list and
falls back to the default when your choice is not offered right now. That line
goes into the log, so a suddenly empty search has an explanation.

## Things to know

- The service guards its API against scripts and hands out rate limits (HTTP
  429). The plugin waits for the announced retry time and tries again; a heavy
  batch may still run into the limit. Normal use, a playlist at a time, stays
  well clear of it.
- It is a community front-end, not an official Qobuz service. It has gone
  offline and returned before, and it can refuse requests from some networks
  (HTTP 403). When that happens the plugin reports itself unavailable and the
  waterfall skips it; your other plugins take over.
- Previews: some releases only expose a 30 second preview to the service. The
  plugin recognizes that and fails the track with a clear message instead of
  saving a preview under the requested quality.
