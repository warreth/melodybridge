# Publishing playlists

A sync job turns a saved playlist into something your players can use: an M3U
file, or a real playlist inside Jellyfin, Plex or Navidrome. Jobs run by hand or
on a schedule, so your "New releases" playlist can show up on the media server
without you touching anything.

<img src="/screens/sync.webp" alt="Sync jobs page with job cards and the five step wizard" width="800">

## Create a sync job

**New sync job** opens a five-step wizard.

### Step 1: Pick your playlist

Give the job a name and choose the source:

- **Saved playlist** publishes a playlist from the Playlists page.
- **Local folder** publishes one scan folder from the Library page, for music
  that did not come from a playlist at all.

### Step 2: Where your music lives

A checkbox list of your scan folders, all checked by default. Leave them all
checked to search every folder for matching files.

### Step 3: Choose the output

| Output | What it needs |
|---|---|
| M3U File | An output path inside the container, for example `/app/playlists/summer.m3u` |
| Jellyfin API | Server URL and an API key. **Test connection** checks the server and lets you pick the user |
| Plex API | Server URL and an X-Plex-Token. The token holder is the only account there is, so there is no user picker |
| Navidrome API | Server URL, username and password. Navidrome has no API keys |

The connection values are stored with the job, so two jobs can point at two
different servers. Named profiles under **Settings, Media servers** are the app
defaults; the liked songs sync uses the profile marked as default.

### Step 4: Optional: fix file paths

Add path or extension remap rules when your player sees files under a different
path than MelodyBridge. This is common in Docker, where the same folder is
mounted at different paths in different containers. A path rule maps a prefix
(`/mnt/BackupHDD/Music` to `/media/music`), and an extension rule rewrites file
extensions (`.flac` to `.opus`). Leave both empty when the paths already line up.

### Step 5: Check and create

Review the summary and press **Create sync job**. Later, **Edit** on the job
card reopens the same wizard with the current values.

## Schedules

Manually created jobs run from the **Run now** button. A job that has a schedule
runs on its own while MelodyBridge is running: the app checks once a minute and
runs every due job, one after another.

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

**Log** shows the last 20 runs with their result and per-track detail. **Run
now** triggers a job immediately, **Edit** changes it, and **Delete** removes
the job and its run history without touching playlists or music files.

## Liked songs become favorites

Tracks that came in through **Import liked songs** are flagged, and every sync
job marks them as favorites on the target: per user in Jellyfin, a top rating on
the Plex token-holder's account, and a star in Navidrome. See
[Accounts and imports](/accounts) for the import side.
