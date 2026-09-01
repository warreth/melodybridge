# User guide

This guide walks through every page of the MelodyBridge web UI.

The home page has two faces. A fresh install gets the intro: a live
checklist with the three steps in order (add a playlist, download the
music, publish it). Each step links to the page where the work happens
and checks off by itself once the database shows the result. **Skip
intro** trades the checklist for the dashboard; finishing all three
steps does the same automatically. The intro never comes back once
dismissed.

## Dashboard

The dashboard is the overview after setup. The four stat cards show the
playlists, downloaded tracks, library files and enabled plugins. Below
them: **Connections** lists Spotify, YouTube, Jellyfin and FlareSolverr
with their current state, **Recent errors** shows the latest failures
with a link to the Logs page, **Recent sync runs** lists the last jobs
with their result, and the playlist cards show each playlist with its
download progress (5/10, complete). Everything refreshes every few
seconds; the Refresh button forces it immediately.

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
list with a filter box; each track shows its state, the real quality of
its file (bitrate, sample rate, container) and, when the quality check
flagged it, a warning next to the entry. **Export CSV** at the top
downloads the track list as a spreadsheet-friendly CSV file.

At the top:

- **Refresh** pulls the playlist from the source again and applies the
  sync mode
- **Download missing** lets the waterfall fetch every track that is not
  present yet

The **CSV** button on each playlist card on the Playlists page exports
the same list without opening the details page.

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
buttons to run it now, view its log, edit it or delete it. **Edit**
reopens the same wizard with the job's current values; saving updates
the job instead of creating a new one.

The run summary counts the whole playlist: "Synced 20/50 tracks, 30
without a local file" means 50 tracks are in the playlist and 20 of
them had a file to publish.

## Library

The Library page covers your own music collection: the folders you told
MelodyBridge to scan. Playlist downloads live on their playlist pages
and never mix into this list.

- The **Tracks** table lists every scanned file with its title, artist,
  real quality (bitrate, sample rate, container), size and the folder it
  sits in; the search box filters title and artist
- **Add location**: point MelodyBridge at a folder with music files
- Each location can scan on a fixed interval in hours or manual only
- **Run scan** starts a scan of all locations immediately

The scanner reads tags, not filenames, so a renamed file is still
recognized. Downloaded tracks are matched against the library: a track
you already own is not downloaded again. The scan history below the
locations shows what each run found, added and updated.

## Settings

The Settings page collects everything that is not per-playlist, in five
tabs.

- **Accounts**: connect Spotify and YouTube. Both only ask for read
  access, see [Accounts and OAuth](accounts.md)
- **Jellyfin**: base URL, API key and default user ID. With no user
  configured, the first non-system user is used
- **Paths**: music path and playlist output folder, as the server sees
  them (inside Docker: `/music` and `/app/playlists`)
- **Quality**: the default audio quality for new playlists (each playlist
  can override it on its own page) and the spectrum check mode: Off, Fast
  (recommended) or Thorough. Quality is two dropdowns: a container and a
  bitrate cap. Auto takes the best each source offers. MP3 plays on every
  device: pick 320 kbps for a home collection, 192 kbps when disk space
  is tight. Opus is the most efficient: 128 kbps sounds transparent to
  most ears, 160 kbps is a safe ceiling. AAC (m4a) fits the Apple world:
  256 kbps is a good pick for phones. FLAC is lossless with no bitrate
  cap and the largest files, best for a media server with plenty of disk.
  A line under the dropdowns explains the trade-off for whichever
  container you pick
- **Network**: the FlareSolverr URL for the Lucida plugin with a Test
  connection button, `off` disables Lucida; plus a log export that
  downloads the most recent 1000 entries as a plain text file

Settings are stored in the database and apply immediately after Save
settings, no restart needed.

## Advanced

The Advanced page holds the knobs most people never need.

- **Sync and scanning**: the auto-scan interval and the sync check
  interval in seconds. These set how often background jobs wake up to
  rescan folders and check auto-sync playlists; the minimums protect
  the CPU
- **Display**: show the file column on playlist tracks, which reveals
  the exact filename behind each track. Useful when hunting inflated
  downloads

## Logs

The Logs page shows what the app did recently: playlist syncs,
downloads, scans and errors.

- When errors exist, a banner at the top lists them; **Show only
  problems** filters the stream down to the errors
- The event stream below shows every entry with its time, level,
  category and message; filter by area or level with the chips (Error
  includes critical) or search with the text box, which also matches
  the friendly area names
- **Copy** puts the filtered entries on the clipboard, **Export**
  downloads them as a file, **Clear** empties the log

When a download fails, the entry holds the plugin, the track and the
reason: rate limited, not found, or rejected by the quality gate. That
is the first place to look when a track did not arrive.
