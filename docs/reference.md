# Settings and options

Everything you can configure in MelodyBridge, gathered in one place. If you are
installing for the first time, start at [Quick start](/quickstart); this page is
the reference.

## Settings

<img src="/screens/settings.webp" alt="Settings page with its seven tabs" width="800">

The Settings page has seven tabs. Changes save with the **Save settings** button
and apply immediately, no restart needed.

### Accounts

Connect Spotify and YouTube here. Both logins only ask for read access, so they
can never change anything on your account. The full walkthrough, including the
redirect URLs and the no-Premium import routes, lives in
[Accounts and imports](/accounts).

### Media servers

Named connection profiles for Jellyfin, Plex and Navidrome: the server URL plus
the credentials each kind needs. Jellyfin and Navidrome take a username and
password. Jellyfin signs in and keeps the session token, or you use **Quick
Connect**: the app shows a code, you approve it in Jellyfin, and the sign-in
finishes by itself. Plex takes an X-Plex-Token. Every profile has a **Test**
button, and one can be marked **Use as app default**.

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
| Lossless | FLAC when a source really has it; a non-strict playlist may step down to the best lossy file |
| No filter | Whatever a source gives, junk included |
| Advanced filters | A container (MP3, FLAC, Opus or AAC) plus a bitrate floor and ceiling |

The primary download is never re-encoded, so a strict filter can make a track
fail when no source can serve that exact format. The track row says so when it
happens.

**Real quality check** verifies every download after the fact. A file whose
length differs from the playlist, or whose bandwidth says the declared bitrate
is inflated, is kept and flagged on its track row for a **Keep** or **Retry**
decision; see [the quality review](/concepts#the-quality-review). Three knobs
set how strict the checks are:

| Setting | Default | What it decides |
|---|---|---|
| Spectrum check | Fast | Off trusts files, Fast analyzes the first minute, Thorough the whole file (capped at 15 minutes) |
| Spectrum strictness | Balanced | How many bitrate classes (96/128/160/192/256/320) below the declared bitrate the measured bandwidth may sit before a file counts as inflated. Strict flags one class, Lenient three |
| Length tolerance (seconds / percent) | 30 / 10 | The allowed length difference, the larger of the two wins. A file outside the window is flagged for a decision, not deleted |

Broken bytes (a file ffprobe cannot read at all) are still deleted and the
track marked failed, and the next run retries it.

### Network

- **FlareSolverr URL**: `auto` (the default) looks for the solver container on
  the Docker network, a URL points at your own solver, and `off` disables Lucida
  entirely. The **Test connection** button tells you what it found. More in
  [Lucida and FlareSolverr](/lucida).
- **Spotify cookie**: optional. Only paste the `sp_dc` value from a logged-in
  browser session when public playlist scraping starts failing for you. It is
  stored in the database, not in an environment variable.

### Notifications

Two ways to hear about events. **Toasts** pop up in the corner when a playlist
download finishes and when a track, sync job or background task fails; both on
by default, and failures always reach the log regardless.

**ntfy push** sends the same events to your own ntfy server. Flip **Send
notifications to ntfy**, enter the **Topic URL** (for example
`https://ntfy.sh/melodybridge`) and, if the topic is protected, an **Access
token**. Then pick what goes out:

- A playlist download run finishes
- A sync job completes
- A sync job fails
- A background task fails: auto-sync, scan, a crashed run

The four switches work independently of the toast toggles: a category can push
without popping up, and the other way around. **Send test notification** sends
one message with the values on screen, even with the enable toggle off. Messages
include track names, and delivery is best effort, so a dead ntfy server never
stalls a download or a scan.

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
| Strict quality | On: only the exact quality is accepted. Off: the download steps down to the best lower quality the enabled plugins declare. See [strict quality](/concepts#strict-quality) |
| Allow container changes | Shown when strict is off: whether a fallback step may leave the chosen container |
| Archive copy | Archive folder and format; empty folder means no archive |
| Auto-sync schedule | Manual, hourly, daily, weekly, monthly or cron |
| Download new tracks after auto-sync | Off by default. After a successful auto-sync refresh, a download run starts when there is pending work; failed tracks are retried every interval. Hidden for file imports |
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
  and tracks waiting for a dedup or quality decision.
- A flagged track row (wrong length or inflated bitrate) shows a **!** pill and
  two buttons: **Keep** accepts the file as it is, **Retry** downloads it again
  while skipping the provider that delivered the flagged file. The file stays
  in the library until you decide. See
  [the quality review](/concepts#the-quality-review).
- **Export CSV** downloads the whole track list as a spreadsheet.
- **Refresh** fetches the playlist again and applies the sync mode.
- **Download missing** starts a download run. While one is live, the button is
  replaced by **Pause**, **Resume** and **Cancel**.
- **Remove playlist** removes the playlist, with an option to delete the
  downloaded files too.

## The Plugins page

<img src="/screens/plugins.webp" alt="Plugins page with order controls, capability badges and live download progress" width="800">

Plugins search for and download your tracks. The order set here is the whole
story for the **No filter** quality. When a playlist asks for a specific
quality, MelodyBridge first picks the plugins that can really deliver it (their
declared capabilities decide) and the order here only breaks ties between
plugins of the same kind. Use the arrows to move a plugin and the switch to
enable or disable it.

- Each plugin card shows its capability badges (formats, bitrate range) and a
  **Settings** row when it has options. The row opens a panel with the fields
  that matter (a token, an API key, a quality) and hides the rarely touched
  knobs (commands, custom folders) behind **Advanced**. Secret fields render
  as password inputs with a reveal button, and values are remembered per
  plugin.
- An unavailable plugin (missing binary, dead service) is skipped by the
  waterfall. The card says what is missing, in one line, and **Refresh status**
  re-checks everything.
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

## The Advanced page <Badge type="warning" text="Advanced" />

<img src="/screens/advanced.webp" alt="Advanced page with download, display, logging and maintenance options" width="800">

The knobs most people never need, in four panels:

- **Downloads**: *Max simultaneous track downloads* (1 to 8, default 2; the next
  run picks up a change), *Plugin time budget* and *Track time budget* (in
  seconds: how long one plugin and one whole track may take before the waterfall
  moves on or gives up) and *Hardlink duplicate tracks across playlists*, the
  dedup toggle described in [How it works](/concepts).
- **Display**: *Show the file column on playlist tracks*, which reveals the
  exact filename behind every track. Handy when hunting inflated downloads.
- **Logging**: *Show database activity in the logs*. Off by default, because
  EF Core writes a line for every SQL command. Warnings and errors show up
  either way. Applies immediately.
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
| *no other provider is available: X delivered the flagged file and was skipped* | You chose Retry on X, and no other enabled plugin can serve this quality. Let a later attempt use X again, or settle the file with Keep before retrying |
| *corrupt download: ffprobe failed* | The file could not be read at all, so it was deleted and the track marked failed; the next run retries |
| *archive copy failed: ffmpeg not found on this host* | The download itself is fine; install ffmpeg if you want archive copies |

A **needs review** state is not a failure either: it means two playlists
disagree about a track's quality and you get to pick, see
[How it works](/concepts).

One more place to look: the **Connections** panel on the dashboard shows the
state of Spotify, YouTube, your media servers and FlareSolverr at a glance.
