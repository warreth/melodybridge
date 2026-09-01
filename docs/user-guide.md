# User guide

This guide walks through every page of the MelodyBridge web UI.

The dashboard follows one idea: three steps, in order. Add a playlist,
download the music, publish the result. Every step shows its own status
on the home page.

## Home

The home page shows whether MelodyBridge is running, how many playlists
are saved, how many tracks are downloaded and how many files the library
holds. The three step cards link to the pages where the work happens:
Playlists, Plugins and Sync jobs. When something fails, the Logs page
shows exactly what went wrong.

## Playlists

The Playlists page holds your saved playlists.

- **Add playlist**: paste a public Spotify or YouTube playlist link.
  MelodyBridge fetches the track list and saves it locally.
- **From my account**: import the playlists of a connected Spotify or
  YouTube account, including private ones and liked songs.
- **Import file**: bring back a previously exported JSON file.
- **Export**: download all playlists as JSON, useful as a backup or to
  move to another instance.

Each playlist card shows the cover, the track count and when it last
synced. Open a card to reach the playlist details page.

### Playlist details

The details page has two halves. The left panel configures the
playlist:

- Name and download folder
- File format (auto, MP3, FLAC, Opus or AAC) and an optional bitrate
  range in kbps, for example `192-320`
- **Auto-sync** toggle with a check interval in minutes
- Sync mode: **Additive** keeps removed tracks as history, **Mirror**
  removes tracks deleted from the source
- **Save settings** stores the changes

The right panel shows the track status (total and downloaded) and live
progress while a download runs. Under **Tracks** you find the full track
list with a filter box; each track shows its state and, when the quality
check flagged it, a warning next to the entry.

At the top:

- **Refresh** pulls the playlist from the source again and applies the
  sync mode
- **Download missing** lets the waterfall fetch every track that is not
  present yet

## Plugins and downloads

The Plugins page (called "Plugins & downloads" in the menu) configures
the plugin waterfall and shows live progress.

- The plugin list is the waterfall: each track is searched through the
  sources in order, and the first plugin that returns a file within
  your quality settings wins
- Move plugins up or down with the arrows, toggle them with the switch
- A plugin marked unavailable (missing binary or service down) is
  skipped automatically; the pill next to it shows the current state
- **Live downloads** at the top shows each running playlist download
  with its progress, the track it is on, and buttons to pause, resume
  or cancel it

The Lucida plugin needs a Cloudflare solver; see
[Lucida and FlareSolverr](lucida.md). Without a solver Lucida stays out
of the waterfall and the other plugins take over.

## Sync jobs

A sync job turns a saved playlist into an M3U file or a Jellyfin
playlist. The New sync job wizard has five steps:

1. Job name and source playlist
2. Search locations: folders to search for the tracks, one per line.
   Leave empty to use every folder from the Library page
3. Output type: M3U File or Jellyfin API, with the output path or
   Jellyfin URL, API key and user ID; plus the schedule: manual, hourly,
   daily or weekly
4. Path and extension remap: only needed when your music player sees
   files under a different path than MelodyBridge, for example inside a
   Docker container
5. Review and create

Each job card shows the last run status and summary, the schedule, and
buttons to run it now, view its log or delete it.

## Library

The Library page manages your scan locations.

- **Add location**: point MelodyBridge at a folder with music files
- Each location can scan on a fixed interval in hours or manual only
- **Run scan** starts a scan of all locations immediately

The scanner reads tags, not filenames, so a renamed file is still
recognized. Downloaded tracks are matched against the library: a track
you already own is not downloaded again. The scan history below the
locations shows what each run found, added and updated.

## Settings

The Settings page collects everything that is not per-playlist.

- **Accounts**: connect Spotify and YouTube. Both only ask for read
  access, see [Accounts and OAuth](accounts.md)
- **Jellyfin**: base URL, API key and default user ID. With no user
  configured, the first non-system user is used
- **Paths**: music path and playlist output folder, as the server sees
  them (inside Docker: `/music` and `/app/playlists`)
- **Music providers**: enable or disable download plugins; the order
  lives on the Plugins page
- **Sync and scanning**: the auto-scan interval (seconds) and the sync
  check interval (seconds) for background services
- **Real quality check**: spectrum mode Off, Fast (recommended) or
  Thorough, plus the Cloudflare solver URL for Lucida (`off` disables
  Lucida)
- **Logs**: export up to 1000 recent log entries as a plain text file

Settings are stored in the database and apply immediately after Save
all settings, no restart needed.

## Logs

The Logs page shows what the app did recently: playlist syncs,
downloads, scans and errors.

- When errors exist, a banner at the top lists them; **Show only
  problems** filters the stream down to the errors
- The event stream below shows every entry with its time, level,
  category and message; filter by area with the chips or search with
  the text box
- **Copy** puts the filtered entries on the clipboard, **Export**
  downloads them as a file, **Clear** empties the log

When a download fails, the entry holds the plugin, the track and the
reason: rate limited, not found, or rejected by the quality gate. That
is the first place to look when a track did not arrive.

## Dev panel

With `DevPanel__Enabled=true` (the dev compose sets this) a `/dev`
page is available with diagnostics for plugin availability and account
state. Keep it off in production.
