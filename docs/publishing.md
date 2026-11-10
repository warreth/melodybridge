# Publishing playlists

A sync job turns a saved playlist into something your players can use: an M3U
file, a real playlist inside Jellyfin, Plex or a Subsonic server, or several of
those at once. One job can write the M3U and push the same playlist to Jellyfin
and Plex in a single run. Jobs run by hand or on a schedule, so your "New
releases" playlist can show up on the media server without you touching
anything.

<img src="/screens/sync.webp" alt="Sync jobs page with job cards, publish profiles and the bulk selection toolbar" width="800">

## Publish playlists in a few clicks

The **Publish** button on the Playlists page is the short path. Tick the
playlists, choose where they go, and pick one schedule. Every ticked playlist
becomes its own job, named after the playlist, with the targets and schedule you
chose. **Send to** can be an M3U file, any number of media server profiles, or
both.

Tick one or more **publish profiles**. Each profile is one destination: an M3U
file or a media server. The first M3U profile is picked for you. **Manage
profiles** opens the Sync jobs page when you need another one. Names that
sanitize to the same file get a numbered suffix, so one job never overwrites
another's file.

Playlists that already have a job are left out of the list, so the same playlist
is not offered twice. Creating the jobs re-reads the list, and anything deleted
in the meantime is named in the result instead of becoming a dangling target.

The quick path applies the same set of publish profiles to every created job.
Open a created job with **Edit** on the Sync jobs page when one of them needs
its own source, targets or advanced options.

<img src="/screens/publish.webp" alt="Quick publish dialog with playlists and publish profiles" width="800">

## Create and edit a sync job

**New sync job** opens one editor, not a step-by-step wizard. The form is one
screen:

- **Basics**: name, source playlist or folder, the search folders, and the
  schedule.
- **Publish to**: tick the publish profiles this job should use. One job can
  publish to several destinations at once, and each destination keeps its own
  rules.
- **Rules and preview**: the rules in use, a sample source path, the player path
  and the exact M3U entry.
- **Advanced options**, folded away at the bottom: a Jellyfin user, and the
  path rules of a legacy target saved before publish profiles. New jobs keep
  their rules on the profiles.

**Create sync job** saves it. Later, **Edit** in a job's menu opens the same
screen with the current values. There is no second editor to hunt for.

<img src="/screens/sync-quick.webp" alt="Sync job editor with basics, publish targets, rules and a live rule preview" width="800">

### Publish profiles

A publish profile is one destination and the rules for it. It sets:

- the kind: M3U file, Jellyfin, Plex or Subsonic;
- for an M3U file: the output folder, file name pattern, text format and
  relative paths;
- for a media server: the connection, the playlist name and the source copy;
- the path remap rules, for example `/music` to `/media/music`;
- the extension remap rules, for example `.flac` to `.mp3`.

Open the **Publish profiles** tab to create one. A job ticks one or more
profiles, so the M3U player and each media server keep their own rules. Changing
the M3U mount never changes what Jellyfin sees. The editor shows a live preview
per profile: the M3U file, the player path and the entry, or the playlist name,
source copy and server path.

Profiles are required for every target. A new install gets a **Default M3U**
profile and one profile per media server connection. Edit those instead of
creating a custom path on the job. A job saved before publish profiles keeps its
legacy target until you open it; the editor then asks for a profile and offers
**Convert to a profile**.

The **Source** field is optional. **Primary library** publishes the normal
download. **Default archive** or a named archive profile publishes that copy
instead. When a track has no copy in the selected archive, the target skips it
and reports it; it never falls back to the primary file. This is how you make a
FLAC playlist next to the normal one, or an Opus playlist for a phone.

Jellyfin playlists are not part of a library. A playlist is a user-facing list
of item IDs, and the items may come from any library. To keep a FLAC playlist
and a normal playlist apart, use two publish profiles with different **Source**
and **Playlist name** values, for example `{job}` and `{job} (FLAC)`.

<img src="/screens/sync-profiles.webp" alt="Publish profiles tab with one profile per destination" width="800">

### Edit many jobs

Choose **Select jobs** to switch the list to rows with checkboxes. Select any
number of jobs, then:

- **Add profile** adds one publish profile to every selected job;
- **Remove profile** removes that target from every selected job;
- **Set schedule** sets their schedule together.

The selection bar works on the selected rows only. Nothing is deleted, and a
job that already has a target keeps its other targets. Removing the last target
leaves the job without a destination; add another profile before its next run.

### Targets at a glance

| Target | What it needs |
|---|---|
| M3U file | A publish profile with kind **M3U file**. **Player compatibility** writes UTF-8 with BOM, Windows line endings and forward slashes for `.m3u8`, uses backslashes for a Windows car or MP4 player, or keeps the plain/old format. **Relative paths** makes entries like `../music/song.flac`; putting the playlist in the same folder as the music gives bare file names and works on the most devices |
| Jellyfin | A publish profile with kind **Jellyfin** and a Jellyfin connection from **Settings, Media servers**. Open **Advanced options** to pick the user whose favorites get marked; that user applies to every Jellyfin target of the job |
| Plex | A publish profile with kind **Plex** and a Plex connection. The token holder is the only account there is, so there is no user picker |
| Subsonic | A publish profile with kind **Subsonic** and a Subsonic or Navidrome connection. Navidrome, gonic, Airsonic, Ampache, Funkwhale and Nextcloud Music all speak this API; there are no API keys |

The job stores the profiles you tick, not a copy of their credentials: edit a
profile once and every job that uses it follows. If a profile is deleted, its
target is removed from the jobs that used it and the editor asks for a
replacement while the job's other targets stay. A job created before profiles
existed keeps the connection it carries in **Advanced options**, where you can
keep or replace it.

Path and extension rules live on the publish profile. A media server usually
matches tracks by metadata, so its profile can leave them empty. An M3U file
contains literal paths, so it needs the rules when the player sees a different
mount. The rules are only needed when that consumer sees files under a different
path or extension, for example inside Docker.

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
last 20 runs and per-track detail, **Edit** for the one editor, and **Delete**,
which removes the job and its run history without touching playlists or music
files.

Many jobs in one list? The **Cards** / **Rows** switch above the list changes
the layout, and **Small**, **Medium** or **Large** sets the card width; both
choices stick. Rows fit more jobs on screen, cards have room for the last run
summary and its progress. The sort control orders jobs by **Last run**,
**Next run** or **Status**. Next run puts due and upcoming jobs first and manual
jobs last; status puts failures and never-run jobs first, so the jobs that need
a look stay at the top.

## Liked songs become favorites

Tracks that came in through **Import liked songs** are flagged, and every sync
job marks them as favorites on the target: per user in Jellyfin, a top rating on
the Plex token-holder's account, and a star on the Subsonic side. See
[Accounts and imports](/accounts) for the import side.
