# Features

## Fetch playlists

<img src="/screens/playlists.webp" alt="Playlists page with playlist cards, covers and download progress" width="800">


Paste a public Spotify or YouTube playlist link and MelodyBridge saves
every track to a local SQLite database, including the platform ID of each
track. No Spotify API key or account is needed: the Spotify source
provider reads the playlist from the public embed page, YouTube
playlists come through yt-dlp. YouTube playlists get their thumbnail as
cover, like Spotify.

::: tip Account login
With the account login ([Accounts and OAuth](accounts.md)) you can also
import what only you can see: private playlists, collaborative
playlists and your liked songs.
:::

## Download through a plugin waterfall

<img src="/screens/playlist-details.webp" alt="Playlist details page with track list, quality info and live download progress" width="800">


Every track is fetched by a waterfall of plugins, tried in order. The
first plugin that finds and quality-gates a track wins. Reorder the
waterfall, toggle plugins and edit their settings on the Plugins page.

| Plugin | What it fetches |
|---|---|
| Lucida | High quality rips from lucida.to (Tidal, Qobuz, Amazon Music and more), optional |
| Monochrome | Community TIDAL rips (FLAC/AAC) via public Hi-Fi API mirrors with automatic fallback |
| DoubleDouble | Multi-service rips (Tidal, Qobuz, Deezer, Amazon) from direct track URLs; captcha-gated, no metadata search |
| SoundCloud | Original uploads by the artist, 128 kbps or better |
| Internet Archive | Public recordings and digitizations, MP3 |
| yt-dlp | YouTube Music first, plain YouTube fallback; best audio as MP3 |

Every plugin quality-gates its files against the requested bitrate band
(minimum and maximum): rips outside it are rejected, so a playlist does
not fill with inflated 128 kbps files labelled as 320 — or thin ones
below the floor you set. When a track fails because every source fell
outside the filters, its row says so, so the fix (relaxing the
filters) is obvious.

::: info Integrity check
A finished file is verified before it counts as downloaded: the
duration must parse and match what the playlist metadata promised. A
corrupt file is deleted, marked failed with the reason, and the next
run retries it.
:::

Plugins that expose settings get an expandable panel on the Plugins
page; values persist per plugin. Playlist downloads run several tracks
in parallel (1 to 8 workers, Advanced page).

## Real quality checks

<img src="/screens/settings.webp" alt="Settings page with the quality check options" width="800">


Some sites hand out files that pretend to be 320 kbps but hold far
less. The spectrum check measures what a file actually contains — its
frequency spectrum, not its file header — and flags tracks whose
spectrum looks inflated for their claimed bitrate.

Pick the mode in Settings:

| Mode | What it checks |
|---|---|
| **Off** | Trusts the file |
| **Fast** | The first minute — default and recommended |
| **Thorough** | The whole file |

## Per-playlist quality targets <Badge type="tip" text="Optional" />

Each playlist picks a target quality from named presets: Space Saver
(any format up to 160 kbps, small files), High Quality (any lossy format
up to 320 kbps, no lossless sizes), Lossless (FLAC when a source has it,
otherwise the best lossy file) or No filter (anything goes). Power
users can open the advanced filters and pick a container format (MP3,
FLAC, Opus or AAC) with a bitrate floor and ceiling instead; the panel
only appears when asked for, and MelodyBridge does not transcode, so
strict filters can make a download fail when the sources cannot
provide that exact format. A failed track says so in its row.

## Consistent tags

Every downloaded file gets a unique `MELODY_ID` written into an ID3v2
TXXX frame, plus title, artist, album and track number taken from the
playlist data. Players show the right names and MelodyBridge can always
recognize its own files, even after you move or rename them.

## Sync modes

Each playlist picks one of two sync modes:

- **Additive** keeps tracks that were removed from the source as flagged
  history. Nothing disappears from your library.
- **Mirror** makes the local copy exactly match the source: tracks that
  vanished from the playlist are removed from the output.

Playlists can also sync themselves on a schedule: manual, hourly,
daily, weekly, monthly or a custom cron expression, the same options
everywhere. A playlist that grows every week can refresh itself every
week.

## Publish playlists

A sync job turns a saved playlist into output for your players:

- **M3U files** with `#EXTINF` metadata, the format every player
  understands
- **Jellyfin playlists**, pushed through the Jellyfin API with the
  configured user
- **Plex playlists**, pushed through the Plex API for the token-holder
- **Navidrome playlists**, pushed through Navidrome's Subsonic API with
  username and password

Tracks imported from your liked songs are flagged, and the sync marks
them as favorites: per user in Jellyfin, a top rating on the Plex
token-holder's account, a star in Navidrome.

## Library scan

<img src="/screens/library.webp" alt="Library page listing scanned music folders" width="800">


Add the folders that hold your music and MelodyBridge scans them, reads
the tags (not the filenames) and keeps the database current. Each
folder runs on its own schedule: manual, hourly, daily, weekly, monthly
or a custom cron expression. Downloaded tracks are matched against the
library, so a track you already own is not downloaded again. A new
playlist registers its download folder as a scan location
automatically.

The Library page lists your scan folders and how matching works;
track lists live on the playlist pages, not here.

## Dashboard

The web UI is a Blazor dashboard with pages for playlists, plugins,
sync jobs, the library and logs:

- **Playlists**: add, import, export, open details, download missing
- **Plugins**: waterfall order, toggles, live download runs with pause and cancel
- **Sync jobs**: the five step wizard from source playlist to output
- **Library**: scan locations and scan runs
- **Settings**: media server profiles, paths, quality checks, accounts
- **Logs**: what happened and what went wrong
