# FLAC Music fallback

FLAC Music is the last source in the shipped waterfall. It searches a
Chinese music front-end for tracks on Kuwo and NetEase Cloud Music by title
and artist. It has no ISRC index and no stable public track URL, so it is the
one built-in source that searches its own catalogue. MelodyBridge keeps it
behind every other source, even when you ask for Lossless.

Use it when a track is missing from the curated sources, not as a primary
source. The site's quality labels do not always match the bytes it serves,
and its catalogue is strongest for Chinese and international pop.

## Setup

1. On the Plugins page, find **FLAC Music (CN)**. It is enabled by default
   and sits last in the order.
2. The **Search platform** setting is **Both (Kuwo + NetEase)** by default.
   Pick one platform when a track only exists there.
3. **Site base URL** under Advanced is only for a mirror.
4. No login is needed.

## What it can deliver

| Tier | What arrives | Notes |
|---|---|---|
| Lossless | FLAC, usually 16-bit / 44.1 kHz, about 900-1100 kbps | The site's lossless tier |
| High | MP3 around 320 kbps | Used when a quality cap allows 320 |
| Standard | MP3 around 128 kbps | Some Kuwo tracks return AAC around 100 kbps instead, despite the MP3 label |

The plugin declares FLAC and MP3 only. An AAC or Opus request never routes
here. A Space Saver request takes the 128 tier, a High Quality request takes
320, and a Lossless request takes the FLAC tier.

The 128 tier is the one exception to the labels. Some Kuwo results answer
with an ADTS AAC stream that the site still calls MP3. An automatic quality
accepts it because the request only names a bitrate cap. An explicit MP3
request rejects the AAC file and the waterfall tries the other platform's
candidate, which is usually the real MP3. If no candidate matches, the track
fails with the normal format message.

## How a download runs

The search asks both platforms for up to 20 results, cleans the Chinese
description suffix from titles such as `Shoot to Thrill-《Iron Man 2》`, and
ranks the candidates on title, artist and duration. The chosen format comes
from your quality request.

A result carries a short-lived sign. The plugin exchanges that sign for a
direct CDN URL. When the sign has expired, it asks for the same song id
again and gets a fresh one; it never re-searches by text during a download.

## When it fails

| Message | What it means |
|---|---|
| *flac.music.hi.cn did not answer* | The site is down or blocked from your network |
| *flac.music.hi.cn no longer lists this track* | The song id disappeared between search and download |
| *flac.music.hi.cn served aac for the requested mp3 tier* | The platform returned its AAC stream for a 128 MP3 row; the waterfall tries the other platform |
| *FLAC Music cannot serve ...* | Your quality request asks for a format or band the site does not have |

The card shows the site's own error when the search fails. The fallback is
only skipped for that track, so the next download can still use it.
