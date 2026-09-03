# User guide

Every page of the MelodyBridge web UI.

The home page has two faces. A fresh install gets the intro: a live
checklist with the three steps in order (add a playlist, download the
music, publish it). Each step links to the page where the work happens
and checks off by itself once the database shows the result. **Skip
intro** trades the checklist for the dashboard; finishing all three
steps does the same automatically. The intro never comes back once
dismissed.

## Dashboard

<img src="/screens/home.webp" alt="Dashboard with stat cards, connections and recent activity" width="800">


The four stat cards show the playlists, downloaded tracks, library
files and enabled plugins. Below them:

- **Connections** lists Spotify, YouTube, Jellyfin, Plex, Navidrome and
  FlareSolverr with their current state
- **Recent errors** shows the latest failures with a link to the Logs
  page
- **Recent sync runs** lists the last jobs with their result
- The playlist cards show each playlist with its download progress
  (5/10, complete)

Everything refreshes every few seconds; the Refresh button forces it
immediately.

## Playlists

<img src="/screens/playlists.webp" alt="Playlists page with playlist cards" width="800">


The Playlists page holds your saved playlists.

- **Add playlist**: paste a public Spotify or YouTube playlist link.
  MelodyBridge fetches the track list and saves it locally.
- **Import**: opens the import panel with three routes.
  - *From your account*: import the playlists of a connected Spotify
    or YouTube account, including private ones and liked songs.
    Needs Spotify Premium (Spotify requires it from developer-app
    owners).
  - *Exportify CSV*: export your liked songs (or any playlist) at
    [exportify.net](https://exportify.net) and upload the CSV here.
    **Recommended without Premium** — works with a free account.
  - *Spotify data export*: request *Download your data* at
    [spotify.com/account/privacy](https://www.spotify.com/account/privacy)
    and upload `YourLibrary.json` (liked songs) or `Playlist1.json`
    (all playlists). Always manual, never automatic: Spotify emails the
    package after up to a few days.

Re-uploading the same file refreshes instead of duplicating.

Each playlist card shows the cover, the track count and when it last
synced. Open a card to reach the playlist details page.

### Playlist details

The details page has two halves. The left panel configures the
playlist:

- Name and download folder
- File format (auto, MP3, FLAC, Opus or AAC) and an optional bitrate
  range in kbps, for example `192-320`
- Auto-sync schedule: **Manual**, **Hourly**, **Daily**, **Weekly**,
  **Monthly** or a custom cron expression, the same options the sync
  jobs and library folders use
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

## Plugins

The Plugins page configures the plugin waterfall and shows live progress.
Plugins search for and download the music: each track is tried through
the enabled plugins in order, and the first one that returns a file
within your quality settings wins.
- Move plugins up or down with the arrows, toggle them with the switch;
  plugins with settings show an expandable **Settings** panel whose
  values are remembered per plugin
- A plugin marked unavailable (missing binary or service down) is
  skipped automatically; the pill next to it shows the current state
- **Live downloads** at the top shows each running playlist download
  with its progress, the track it is on, and buttons to pause, resume
  or cancel it. Several tracks of the same playlist download in
  parallel; the number of simultaneous downloads is set on the
  Advanced page (1 to 8, default 2)
- Every completed file gets an integrity check: the duration must parse
  and match the playlist metadata. A corrupt file is deleted, marked
  failed with the reason, and retried on the next run

::: info Lucida needs a Cloudflare solver
See [Lucida and FlareSolverr](lucida.md). Without a solver Lucida stays
out of the waterfall and the other plugins take over.
:::

Monochrome and DoubleDouble are community rip mirrors: Monochrome
searches TIDAL and falls back between its instances automatically,
DoubleDouble handles direct track URLs only (its search is
captcha-gated, so it never finds tracks by name).

## Sync jobs

A sync job turns a saved playlist or a local folder into an M3U file or
a playlist on Jellyfin, Plex or Navidrome. The New sync job wizard has
five steps:

1. **Job name and source.** The source is a saved playlist or, with
   Local folder, a single scan folder from the Library page
2. **Search locations:** a checkbox list of your scan folders, all
   checked by default. Leave them all checked to use every folder
3. **Output type:** M3U File, Jellyfin, Plex or Navidrome. M3U needs
   the output path. The servers each take their own connection
   fields: Jellyfin needs the server URL and API key, plus a Test
   connection button that checks the server and lists its users for
   you to pick one. Plex needs the server URL and an X-Plex-Token
   (the token-holder is the only user, so there is no user picker).
   Navidrome needs the server URL, a username and a password. The
   connection values are stored per job, so two jobs can point at
   two different servers. The schedule is manual, hourly, daily,
   weekly, monthly or cron; cron takes a five-field expression like
   `0 3 * * *` (03:00 nightly)
4. **Path and extension remap rules:** add as many as you need, each
   with a from and a to value. Only needed when your music player
   sees files under a different path than MelodyBridge, for example
   inside a Docker container — the paths must match what the server
   sees, such as its container mount
5. **Review and create**

Each job card shows the last run status and summary, the schedule, and
buttons to run it now, view its log, edit it or delete it. **Edit**
reopens the same wizard with the job's current values; saving updates
the job instead of creating a new one.

::: tip Reading the run summary
"Synced 20/50 tracks, 30 without a local file" counts the whole
playlist: 50 tracks are in it and 20 of them had a file to publish.
The Log view breaks those 30 down per track, so you can see exactly
which files are missing or were not found on the media server.
:::

## Library

The Library page covers your own music collection: the folders you told
MelodyBridge to scan. Playlist downloads live on their playlist pages
and never mix into this list — the Library page shows your folders and
how matching works, not a track list.

- **Add location**: point MelodyBridge at a folder with music files
- A new playlist registers its download folder as a scan location
  automatically
- Each location can scan on a fixed interval in hours or manual only
- **Run scan** starts a scan of all locations immediately

The scanner reads tags, not filenames, so a renamed file is still
recognized. Downloaded tracks are matched against the library: a track
you already own is not downloaded again. The scan history below the
locations shows what each run found, added and updated.

## Settings

The Settings page collects everything that is not per-playlist, in six
tabs.

- **Accounts**: connect Spotify and YouTube. Both only ask for read
  access, see [Accounts and OAuth](accounts.md)
- **Media servers**: named connection profiles for Jellyfin, Plex and
  Navidrome that several sync jobs can share. Add and edit profiles
  in one place, each with a Test button and a Use as app default
  switch
- **Paths**: music path and playlist output folder, as the server sees
  them (inside Docker: `/music` and `/app/playlists`)
- **Quality**: the default audio quality for new playlists (each
  playlist can override it on its own page) and the spectrum check
  mode. Quality is three dropdowns: a container, a bitrate floor and
  a bitrate ceiling. Auto takes the best each source offers and locks
  the bitrate dropdowns.
- **Network**: the FlareSolverr URL for the Lucida plugin with a Test
  connection button, `off` disables Lucida; plus a log export that
  downloads the most recent 1000 entries as a plain text file
- **About**: app version and update check

::: tip Choosing a container
MP3 plays on every device: pick 320 kbps for a home collection,
192 kbps when disk space is tight. Opus is the most efficient: 128 kbps
sounds transparent to most ears, 160 kbps is a safe ceiling. AAC
(m4a) fits the Apple world: 256 kbps is a good pick for phones. FLAC
is lossless, locks the bitrate dropdowns and has the largest files,
best for a media server with plenty of disk. A line under the
dropdowns explains the trade-off for whichever container you pick.
:::

Settings are stored in the database and apply immediately after Save
settings, no restart needed.

## Advanced <Badge type="warning" text="Advanced" />

The Advanced page holds the knobs most people never need.

- **Downloads**: the maximum number of simultaneous track downloads.
  More finishes large playlists faster but hits every source harder.
  When playlists and library folders refresh is not a knob here: each
  playlist and folder carries its own schedule on its own page
- **Display**: show the file column on playlist tracks, which reveals
  the exact filename behind each track. Useful when hunting inflated
  downloads
- **Database activity**: show database activity in the logs. Off by
  default, it hides the constant Executed DbCommand lines EF Core
  writes to the Logs page; warnings and errors stay visible either
  way. Turn it on only while debugging a database problem
- **Library maintenance**: the **Recompute audio facts** button
  refills the bitrate, sample rate and size of older downloads that
  predate the probing, and flags files missing on disk for a
  re-download

## Logs

<img src="/screens/logs.webp" alt="Logs page with the filterable event stream" width="800">


The Logs page shows what the app did recently: playlist syncs,
downloads, scans and errors.

- When errors exist, a banner at the top lists them; **Show only
  problems** filters the stream down to the errors
- The event stream below shows every entry with its time, level,
  category and message; filter by area or level with the chips (Error
  includes critical) or search with the text box, which also matches
  friendly area names
- **Copy** puts the filtered entries on the clipboard, **Export**
  downloads them as a file, **Clear** empties the log

::: tip When a download fails
The entry holds the plugin, the track and the reason: rate limited,
not found, or rejected by the quality gate. That is the first place
to look when a track did not arrive.
:::
