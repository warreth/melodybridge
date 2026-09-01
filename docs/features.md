# Features

## Fetch playlists

Paste a public Spotify or YouTube playlist link and MelodyBridge saves
every track to a local SQLite database, including the platform ID of each
track. No Spotify API key or account is needed: the Spotify source
provider reads the playlist from the public embed page, YouTube
playlists come through yt-dlp.

With the account login ([Accounts and OAuth](accounts.md)) you can also
import what only you can see: private playlists, collaborative playlists
and your liked songs.

## Download through a plugin waterfall

Every track is fetched by a waterfall of plugins, tried in order. The
first plugin that finds and quality-gates a track wins. You can reorder
the waterfall and toggle plugins on the Downloads page.

| Plugin | What it fetches |
|---|---|
| Lucida | High quality rips from lucida.to (Tidal, Qobuz, Amazon Music and more), optional |
| SoundCloud | Original uploads by the artist, 128 kbps or better |
| Internet Archive | Public recordings and digitizations, MP3 |
| yt-dlp | YouTube and YouTube Music, best audio as MP3 |

Every plugin quality-gates its files: low bitrate rips are rejected so a
 playlist does not fill with inflated 128 kbps files labelled as 320.

## Real quality checks

Some sites hand out files that pretend to be 320 kbps but hold far less.
The spectrum check measures what a file actually contains by looking at
its frequency spectrum, not its file header, and flags tracks whose
spectrum looks inflated for their claimed bitrate.

You pick the mode in Settings: **Off** trusts the file, **Fast** checks
the first minute, **Thorough** checks the whole file. Fast is the default
and the recommended setting.

## Per-playlist quality targets

Each playlist can ask the waterfall for a specific container format
(auto, MP3, FLAC, Opus or AAC) and an optional bitrate ceiling. Lossless
formats ignore the ceiling because a cap makes no sense there.

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

Playlists can also sync on their own interval, so a playlist that grows
every week can refresh itself every week.

## Publish playlists

A sync job turns a saved playlist into output for your players:

- **M3U files** with `#EXTINF` metadata, the format every player
  understands
- **Jellyfin playlists**, pushed through the Jellyfin API with the
  configured user

Tracks imported from your liked songs are flagged, and the Jellyfin sync
marks them as favorites for the configured user.

## Library scan

Add the folders that hold your music and MelodyBridge scans them, reads
the tags (not the filenames) and keeps the database current. Scans can
run on an interval per folder or manually. Downloaded tracks are matched
against the library, so a track you already own is not downloaded again.

## Dashboard

The web UI is a Blazor dashboard with pages for playlists, plugins,
sync jobs, the library and logs:

- **Playlists**: add, import, export, open details, download missing
- **Plugins**: waterfall order, toggles, live download runs with pause and cancel
- **Sync jobs**: the five step wizard from source playlist to output
- **Library**: scan locations and scan runs
- **Settings**: Jellyfin, paths, intervals, quality checks, accounts
- **Logs**: what happened and what went wrong
