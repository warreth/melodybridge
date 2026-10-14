# Settings and options

Everything you can configure in MelodyBridge, gathered in one place. If you are
installing for the first time, start at [Quick start](/quickstart); this page is
the reference.

## Settings

<img src="/screens/settings.webp" alt="Settings page with its six tabs" width="800">

The Settings page has six tabs. Changes save with the **Save settings** button
and apply immediately, no restart needed.

### Accounts

Connect Spotify and YouTube here. Both logins only ask for read access, so they
can never change anything on your account. The full walkthrough, including the
redirect URLs and the no-Premium import routes, lives in
[Accounts and imports](/accounts).

### Media servers

Named connection profiles for Jellyfin, Plex and Navidrome: server URL plus the
credentials each kind needs (API key, Plex token, or username and password for
Navidrome). Every profile has a **Test** button, and one can be marked **Use as
app default**.

Profiles are shortcuts, not shared state: a sync job carries its own connection
fields, and the default profile is used by the liked songs sync.

### Default paths

- **Music path** (`/music` in the Docker image): where downloads land.
- **Playlist output folder** (`/app/playlists`): where M3U files are written.
- **Archive path** and **Archive format**: the defaults for archive copies.
  Leave the path empty for no archive. Per-playlist values override these.

Remember that these are paths inside the container, not on your host. The page
shows a reminder next to the fields.

### Quality

**Default audio quality** is what new playlists start with; each playlist can
override it on its own page.

| Preset | What it accepts |
|---|---|
| Space Saver | Any format up to 160 kbps. Small files, made for huge playlists |
| High Quality | Any lossy format up to 320 kbps, no lossless sizes |
| Lossless | FLAC when a source has it, otherwise the best lossy file |
| No filter | Whatever a source gives, junk included |
| Advanced filters | A container (MP3, FLAC, Opus or AAC) plus a bitrate floor and ceiling |

The primary download is never re-encoded, so a strict filter can make a track
fail when no source can serve that exact format. The track row says so when it
happens.

**Real quality check** holds the spectrum check mode: **Off** to trust files,
**Fast** to analyze the first minute (the default), or **Thorough** for the whole
file. See [How it works](/concepts) for what it measures.

### Network

- **FlareSolverr URL**: `auto` (the default) looks for the solver container on
  the Docker network, a URL points at your own solver, and `off` disables Lucida
  entirely. The **Test connection** button tells you what it found. More in
  [Lucida and FlareSolverr](/lucida).
- **Spotify cookie**: optional. Only paste the `sp_dc` value from a logged-in
  browser session when public playlist scraping starts failing for you. It is
  stored in the database, not in an environment variable.

### About

App version and **Check for updates**, a button to replay the guided tour, the
accent color picker, **Export logs** (the most recent 1000 entries as a text
file), and the database section: **Export zip** makes a snapshot, and importing
one replaces the database. After a database import, restart the app.

## Playlist options

Open a playlist card and the left panel holds everything specific to it:

| Option | Notes |
|---|---|
| Name | Shown everywhere in the app |
| Download folder | Empty means the default music path |
| Audio quality | One of the presets above, or advanced filters |
| Archive copy | Archive folder and format; empty folder means no archive |
| Auto-sync schedule | Manual, hourly, daily, weekly, monthly or cron |
| Sync mode | Additive keeps removed tracks as history, Mirror matches the source |

There is no Save button. Text fields save half a second after you stop typing,
dropdowns save right away, and a small pill narrates the state: **Saving…**,
**Saved**, or **Failed to save**. A failed save rolls the fields back to what
the database actually has.

Playlists imported from a file are snapshots: the header says so, and updating
them means re-importing the file.

### The track list

Each track row shows its state and, once downloaded, the real quality: bitrate,
sample rate, format and file size. Enable **Show the file column on playlist
tracks** on the Advanced page to see the file names, including
`archive: <name>` chips for archive copies.

- The filter box narrows the list; **Needs attention** shows only failed tracks
  and tracks waiting for a dedup decision.
- **Export CSV** downloads the whole track list as a spreadsheet.
- **Refresh** fetches the playlist again and applies the sync mode.
- **Download missing** starts a download run. While one is live, the button is
  replaced by **Pause**, **Resume** and **Cancel**.
- **Delete playlist** removes the playlist, with an option to delete the
  downloaded files too.

## The Plugins page

<img src="/screens/plugins.webp" alt="Plugins page with order controls, capability badges and live download progress" width="800">

Plugins search for and download your tracks. The order set here applies to the
**No filter** quality; other presets route by fit first, trying lossless
sources first for Lossless and small-file sources first for Space Saver. Use the
arrows to move a plugin and the switch to enable or disable it.

- Each plugin card shows its capability badges (formats, bitrate range) and an
  expandable **Settings** panel when it has options. Values are remembered per
  plugin.
- An unavailable plugin (missing binary, dead service) is skipped by the
  waterfall. The card explains what to fix, and **Refresh status** re-checks
  everything.
- **Live downloads** shows each running playlist with its progress and the track
  it is on, plus **Pause**, **Resume** and **Cancel** buttons. Several tracks of
  a playlist download in parallel; the limit is set on the Advanced page. Every
  track is attempted once per run, so one broken track cannot stall the rest.

## The Library page

<img src="/screens/library.webp" alt="Library page with scan folders and the track identity panel" width="800">

The Library page covers music you already own. **Add location** points
MelodyBridge at a folder; new playlists register their download folder here
automatically.

- Each location has its own schedule (manual, hourly, daily, weekly, monthly or
  cron) and an optional **Live monitoring** toggle that rescans when files
  change. **Run scan** does all folders immediately.
- The scanner reads tags, not filenames, and matches files to playlist tracks by
  their `MELODY_ID` tag, so a song you already have is never downloaded again.
  Location cards show how many files with a `MELODY_ID` they hold, the schedule,
  the last scan, and a **path missing** pill when the folder went away.
- The **Track identity** panel on the right repeats the short version of how
  matching works. The long version is in [How it works](/concepts).

## The Advanced page

<img src="/screens/advanced.webp" alt="Advanced page with download, display, logging and maintenance options" width="800">

The knobs most people never need, in five groups:

- **Downloads**: *Max simultaneous track downloads* (1 to 8, default 2; the next
  run picks up a change) and *Hardlink duplicate tracks across playlists*, the
  dedup toggle described in [How it works](/concepts).
- **Display**: *Show the file column on playlist tracks*, which reveals the
  exact filename behind every track. Handy when hunting inflated downloads.
- **Logging**: *Show database activity in the logs*. Off by default, because
  EF Core writes a line for every SQL command. Warnings and errors show up
  either way. Applies immediately.
- **Notifications**: toasts for finished playlist downloads and for failed
  tracks or sync jobs. Both on by default; failures always reach the log
  regardless.
- **Maintenance**: **Recompute audio facts** reads bitrate, sample rate and size
  straight from the files on disk, for downloads that predate the probing. Tracks
  whose file has vanished are queued for a re-download.

## The Logs page

<img src="/screens/logs.webp" alt="Logs page with the filterable event stream" width="800">

The event stream shows playlist syncs, downloads, scans and errors. When there
are errors, a banner at the top lists them; click one to jump to that area.

- Filter by area (Playlists, Downloads, Sources, Library, Sync, Database,
  System) or level (Info, Warning, Error). The search box also matches the
  friendly area names.
- **Show only problems** narrows everything down to errors. **Copy** puts the
  filtered entries on your clipboard, **Export** writes them to a file, and
  **Clear** empties the buffer.

## When a download fails

The error on a track row is the reason the waterfall gave up:

| Message | What to try |
|---|---|
| *your quality filters excluded every plugin* | No enabled plugin can serve that format or band. Loosen the filter or enable more plugins |
| *sources had N file(s) outside your quality filters* | Candidates existed, but none inside your band. Move the floor or ceiling |
| *sources only returned files in another format* | The filter asks for one container and the sources serve others |
| *sources only had versions with the wrong length* | Nothing matched the expected duration, likely a different edit of the track |
| *sources returned files that are not audio* | The plugin got junk; it was deleted. Retry, the waterfall may pick another source next run |
| *no source had this track* | Nothing matched at all. Check the spelling in the source playlist |
| *corrupt download: duration mismatch* | The file was deleted and the track marked failed; the next run retries |
| *archive copy failed: ffmpeg not found on this host* | The download itself is fine; install ffmpeg if you want archive copies |

A **needs review** state is not a failure: it means two playlists disagree about
a track's quality and you get to pick, see [How it works](/concepts).

One more place to look: the **Connections** panel on the dashboard shows the
state of Spotify, YouTube, your media servers and FlareSolverr at a glance.
