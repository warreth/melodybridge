# Publishing playlists

A sync job turns a saved playlist into something your players can use: an M3U
file, a real playlist inside Jellyfin, Plex or a Subsonic server, or several of
those at once. One job can write the M3U and push the same playlist to Jellyfin
and Plex in a single run. Jobs run by hand or on a schedule, so your "New
releases" playlist can show up on the media server without you touching
anything.

<img src="/screens/sync.webp" alt="Sync jobs page with jobs, rule profiles and bulk selection" width="800">

## Publish playlists in a few clicks

The **Publish** button on the Playlists page is the short path. Tick the
playlists, choose where they go, and pick one schedule. Every ticked playlist
becomes its own job, named after the playlist, with the targets and schedule you
chose. **Send to** can be an M3U file, any number of media server profiles, or
both. The M3U files land in the **Playlist output folder** setting, one per
playlist, as `.m3u8` files with a UTF-8 BOM and Windows line endings. Edit the
job afterwards to switch it to `.m3u` or another format. Names that sanitize to
the same file get a numbered suffix, so one job never overwrites another's file.
**Add profile** opens Settings, Media servers, when you need another target
first.

Playlists that already have a job are left out of the list, so the same playlist
is not offered twice; creating the jobs re-reads the list, and anything deleted
in the meantime is named in the result instead of becoming a dangling target.

The quick path still skips custom remaps and the per-job Jellyfin user. Open a
created job with **Quick edit** on the Sync jobs page, or use a rule profile.

<img src="/screens/publish.webp" alt="Quick publish dialog on the Playlists page" width="800">

## Create and edit a sync job

**New sync job** opens the quick editor, not a five-step wizard. The form is one
screen:

- **Basics**: name, source playlist or folder, and the search folders.
- **Publish to**: the M3U file, a rule profile, the compatibility format, and
  any media server profiles. One job can publish to several targets at once.
- **Schedule**: manual, hourly, daily, weekly, monthly or cron.
- **Rules and preview**: the shared rules in use, a sample source path, the
  player path and the exact M3U entry.

**Create sync job** saves it. Later, **Quick edit** in a job's menu opens the
same screen with the current values. **Advanced edit** opens the full editor for
custom per-job remaps, legacy connections and every low-level option.

<img src="/screens/sync-quick.webp" alt="Quick sync job editor with basics, targets, rules and a live rule preview" width="800">

### Rule profiles

A rule profile is a reusable bundle of output rules. It sets:

- the M3U output folder;
- the file name pattern, with `{job}` or `{playlist}` for the job name;
- the text format, relative paths and UTF-8 BOM/CRLF/backslash choices;
- the path remap rules, for example `/music` to `/media/music`;
- the extension remap rules, for example `.flac` to `.mp3`.

Open the **Rule profiles** tab to create one. Every job linked to that profile
follows it on the next run, so changing the library mount or the playlist folder
is one edit, not twenty. The editor shows a live preview: the M3U file, the path
the player sees, and the M3U entry that will be written.

The profile is also used by the Jellyfin, Plex and Subsonic path lookup. One set
of rules therefore keeps the M3U and every media server target in step.

<img src="/screens/sync-profiles.webp" alt="Rule profiles tab with a shared output profile" width="800">

### Edit many jobs

Choose **Select jobs** to switch the list to rows with checkboxes. Select any
number of jobs, then:

- **Apply profile** links them all to one rule profile;
- **Set schedule** sets their schedule together;
- **Set M3U folder** gives each selected job its own `{job}.m3u8` in that folder
  and switches them to custom paths;
- **Add server** adds one media server profile to every selected job.

The selection bar works on the selected rows only. Nothing is deleted, and a
job that already has a target keeps its other targets.

### The advanced editor

The old wizard is still available from **Advanced wizard** and **Advanced edit**.
Use it when a job needs its own remap rules instead of a shared profile, or when
you need to keep a legacy inline connection. It walks through source, search
folders, targets, remaps and review.

For a step-by-step description of the targets:

| Target | What it needs |
|---|---|
| M3U file | An output path inside the container. With a rule profile the folder and file name come from the profile; without one, type the path. **Playlist compatibility** writes UTF-8 with BOM, Windows line endings and forward slashes for `.m3u8`, uses backslashes for a Windows car or MP4 player, or keeps the plain/old format. **Relative paths** makes entries like `../music/song.flac`; putting the playlist in the same folder as the music gives bare file names and works on the most devices |
| Jellyfin | A Jellyfin profile from **Settings, Media servers**. **Test connection** checks it and lets you pick the user; that user applies to every Jellyfin target of the job |
| Plex | A Plex profile. The token holder is the only account there is, so there is no user picker |
| Subsonic | A Subsonic or Navidrome profile. Navidrome, gonic, Airsonic, Ampache, Funkwhale and Nextcloud Music all speak this API; there are no API keys |

The job stores the profiles you tick, not a copy of their credentials: edit a
profile once and every job that uses it follows. If a profile is deleted, the
next edit drops that target and asks for a replacement while the job's other
targets stay. A job created before profiles existed keeps the connection it
carries as a target you can keep or replace.

Path and extension rules apply to the M3U entries and to the path lookup for
Jellyfin, Plex and Subsonic. They are only needed when those consumers see files
under a different path or extension, for example inside Docker.

## Schedules

Manually created jobs run from the **Run now** button. **Run all now** at the
top of the page runs every job once, one after the other, and says how many
finished. A job that has a schedule runs on its own while MelodyBridge is
running: the app checks once a minute and runs every due job, one after
another.

A new job first runs at its next scheduled time, not the moment you save it. Pick
Daily and the first run is the next 03:00; the card says when that is (**next
run ...**, or **due now** when a missed window is about to catch up). Press
**Run now** to start it earlier. A job that was created while the app was off
catches up on the next check.

Pick **Manual**, **Hourly**, **Daily**, **Weekly**, **Monthly**, or **Cron** for
something custom. Cron uses five fields (minute, hour, day of month, month, day
of week):

- `0 * * * *` every hour on the hour
- `0 3 * * *` every night at 03:00
- `0 3 * * 1` Mondays at 03:00
- `30 4 1 * *` the first of the month, 04:30

There is no separate daemon: jobs, playlist auto-sync and library scans all run
only while the app process is up.

## Reading a run

The job card shows the last status and a one-line summary. That summary counts
the whole playlist:

::: tip Reading "Synced 20/50 tracks, 30 without a local file"
The playlist has 50 tracks, and 20 of them had a file on disk to publish. The
other 30 have no local file yet, usually because they were never downloaded.
**Log** breaks the run down per track, so you can see which ones are missing.
:::

**Run now** triggers a job immediately. The **...** menu opens **Log** with the
last 20 runs and per-track detail, **Edit** for the wizard, and **Delete**,
which removes the job and its run history without touching playlists or music
files.

Many jobs in one list? The **Cards** / **Rows** switch above the list changes
the layout; the choice sticks. Rows fit more jobs on screen, cards have room
for the last run summary and its progress. The sort control orders jobs by
**Last run**, **Next run** or **Status**. Next run puts due and upcoming jobs
first and manual jobs last; status puts failures and never-run jobs first, so
the jobs that need a look stay at the top.

## Liked songs become favorites

Tracks that came in through **Import liked songs** are flagged, and every sync
job marks them as favorites on the target: per user in Jellyfin, a top rating on
the Plex token-holder's account, and a star on the Subsonic side. See
[Accounts and imports](/accounts) for the import side.
