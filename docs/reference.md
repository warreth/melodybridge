# Settings and options

Everything you can configure in MelodyBridge, gathered in one place. If you are
installing for the first time, start at [Quick start](/quickstart); this page is
the reference.

## Settings

<img src="/screens/settings.webp" alt="Settings page with its seven tabs" width="800">

The Settings page has seven tabs. Changes save with the **Save settings** button
and apply immediately, no restart needed. The search box above the tabs jumps to
the tab that owns a setting: type `theme`, `ntfy`, `Jellyfin` or a similar word
and pick a result. It is a jump list, not a live filter of every field.

### Accounts

Connect Spotify, YouTube and Apple Music here. The Spotify and YouTube logins
only ask for read access, so they can never change anything on your account.
One **Spotify web session** card holds a single `sp_dc` cookie for two jobs: the
public page fallback and free account imports. Paste the cookie and press
**Save for public fallback** when you only want the safer scraping fallback.
Tick the acknowledgement and press **Enable account imports** to test the
unofficial route.

The **Apple Music** web session card takes a `media-user-token` cookie from a
logged-in music.apple.com browser. Public and shared playlist links need no
cookie; connect it for unshared library playlists and saved songs. The full
walkthrough, including the redirect URLs, both cookie routes and the file
imports, lives in [Accounts and imports](/accounts).

### Media servers

Named connection profiles for Jellyfin, Plex and Subsonic (Navidrome, gonic,
Airsonic and friends): the server URL plus the credentials each kind needs.
Jellyfin and Subsonic take a username and
password. Jellyfin signs in and keeps the session token, or you use **Quick
Connect**: the app shows a code, you approve it in Jellyfin, and the sign-in
finishes by itself. Plex takes an X-Plex-Token. Every profile has a **Test**
button, and one can be marked **Use as app default**.

Connections are what publish profiles pick: the profile editor lists the
connections of the chosen kind, and a publish profile stores the connection, not
a copy of its credentials. Edit a connection once and every publish profile that
uses it follows. The connection marked as app default feeds the app-wide
fallback the sync clients use when a publish profile has no connection of its
own.

### Publish profiles

One destination and its rules: M3U file, Jellyfin, Plex or Subsonic. An M3U
profile sets the output folder, file name pattern, text format and relative
paths. A media server profile sets the connection, playlist name and source
copy. Every profile can set path remaps and extension remaps. Open the
**Publish profiles** tab on the Sync jobs page to create one. A job ticks one or
more profiles, so each destination keeps its own rules. A linked job follows the
profile live, so changing the library mount or playlist folder updates every job
that uses it. The editor shows the resolved M3U path and a preview of the player
path and M3U entry, or the playlist name, source copy and server path.

**Source** is optional: **Primary library** publishes the normal download,
**Default archive** or a named archive profile publishes that copy instead.
Missing archive copies are skipped and reported, never replaced by the primary
file. **Select jobs** adds or removes a publish profile and sets a schedule for
several jobs at once.

### Default paths

<img src="/screens/settings-paths.webp" alt="Default paths tab with the music path, playlist folder and the default archive profile" width="800">

- **Music path** (`/music` in the Docker image): the root for downloads. With
  **Sort downloads into a folder per playlist** on, the default, each playlist
  gets its own folder below it named after the playlist, so one playlist's
  files never mix with another's. Turn the toggle off and every download lands
  directly in the music path. Saving a new path moves your existing library
  along with it.
- **Playlist output folder** (`/app/playlists`): where M3U files are written.
  A new install gets a **Default M3U** publish profile with
  `<folder>/<job name>.m3u8`. Edit that profile, or create another one, when a
  job should land elsewhere. A publish profile can set the folder and file name
  for every job that ticks it.
- **Default archive**: the first archive profile, marked **default**, edited
  from its own row. Leave the path empty for no default archive. Playlists
  choose archive destinations by profile. A playlist from before that checklist
  keeps its old folder and format as an **Old playlist archive** row until you
  save a new selection on the playlist page. The format picker offers Auto,
  Opus, FLAC, MP3 and AAC, plus Custom for another container ffmpeg can write
  (for example ogg, wav or m4a).
  Add a bitrate after a colon for lossy presets (`mp3:160`, `opus:192`) or a
  codec (`m4a:alac`). A format ffmpeg cannot write leaves a warning on the
  track. Each playlist gets its own folder under the archive path while the
  sort toggle is on. Saving a new default path moves the existing default
  copies along with it.

<img src="/screens/settings-archives.webp" alt="Archive profiles with the default row and two extra copies, each with its folder and format" width="800">

- **Extra archive profiles** sit below the default row. Add as many as you
  need, each with a name, folder, container and bitrate. A playlist selects
  which destinations it writes, so one library can keep an Opus 128 copy and an
  MP3 160 copy next to the default. Extra profiles have their own enable
  toggle, **Build missing** button and delete action. **Build missing** scans
  the existing library and creates the copies that profile does not have yet
  for the playlists that select it; it runs in the background, shows its
  progress and ETA here and on Needs attention, and only visits copies that are
  actually missing. A copy that fails lands in the **Archive copies** group on
  Needs attention. The default has no bulk backfill: recreate one copy at a
  time from the track row or the Needs attention page. Disabling a profile
  keeps its files on disk.
  Deleting one removes the profile and its copy rows, not the files, and
  drops the scan location it registered unless another profile still uses
  that folder. The playlist page selects the default archive and any named
  profiles as one checklist, so a playlist can write no copies or several.
  A publish profile can use one of these profiles as its **Source**, so an M3U
  file or a media server playlist publishes that copy instead of the primary
  file.

The path fields have a folder button that opens a server-side picker with
shortcuts to the music, playlist, archive and scan folders. You can still type a
path by hand. The fields show a grey `{playlist}` suffix when the folder rule
appends a playlist name, so what a field resolves to is visible before you save.

Remember that these are paths inside the container, not on your host. The page
shows a reminder next to the fields. Changing a path moves the tracked files,
the playlist folders and the scan locations; a file whose destination is already
taken stays where it is and keeps its row, so nothing is overwritten.

### Quality

**Default audio quality** is what every new playlist starts with, whichever route
created it: the add form, an account import, liked songs or a file import. Each
playlist can override it on its own page.

| Preset | What it accepts |
|---|---|
| Space Saver | Any format up to 160 kbps. Small files, made for huge playlists |
| High Quality | Any lossy format up to 320 kbps, no lossless sizes |
| Lossless | FLAC when a source really has it; a non-strict playlist may step down to the best lossy file |
| No filter | Whatever a source gives; no quality gate |
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

Broken bytes (a file ffprobe cannot parse) are still deleted and the track
marked failed, and the next run retries it. A failed or missing ffprobe is not
proof of broken bytes: the file stays and the row says it could not be verified.

### Network

- **FlareSolverr URL**: `auto` (the default) looks for the solver container on
  the Docker network, a URL points at your own solver, and `off` disables Lucida
  entirely. **Test connection** resolves the endpoint and solves `lucida.to`
  for real, so a healthy HTTP port that cannot finish a challenge is reported
  honestly. More in [Lucida and FlareSolverr](/lucida).
- The Spotify fallback cookie lives on the **Accounts** tab with the web session
  card. One cookie covers public scraping and free-account imports. The Apple
  Music web session is a separate card in the same tab.

### Notifications

Two ways to hear about events. **Toasts** pop up in the corner when a playlist
download finishes and when a track, sync job or background task fails; both on
by default, and failures always reach the log regardless.

A success or info toast fades after a few seconds. A warning or error stays for
12 seconds and can be closed sooner. Routine toasts do not enter the bell: the
bell is a short list of things that still need a decision, such as a plugin
login that stopped working, a failed sync job, a background failure or an
update. Those entries stay until you dismiss them or the condition clears. Open
the bell to follow an action such as **Fix plugin**, **Open sync jobs**,
**Release notes** or **View logs**, dismiss one row, or clear the list. The bell
badge counts important entries you have not opened yet.

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

### Appearance

<img src="/screens/settings-appearance.webp" alt="Appearance tab with Light, Dark and Auto plus the nine accent colors and the interface mode switch" width="800">

The Appearance tab holds the look of the app. **Theme** offers Light, Dark or
Auto, where Auto follows your device. The accent picker offers nine palettes:
teal, cyan, blue, green, violet, rose, orange, amber and slate. Each swatch
carries its name, and the choice colors tabs, buttons, highlights and the
guided tour. **Interface** switches between **Simple**, which keeps the sidebar
to the everyday tools, and **Advanced**, which also shows the library,
diagnostics and logs. Simple also starts the plugin page in rows and trims the
dashboard and attention page. A first install asks for the mode; the same
switch sits under the brand at the top of the sidebar. Theme and accent are stored in this
browser only.

### About

App version and **Check for updates**, a button to replay the guided tour, and
the database backup.

MelodyBridge checks GitHub once a day for a newer release and adds a notification
with a link to the notes when one exists. After an upgrade it opens that
version's release notes once; a first install has nothing to announce. Both
checks send only a request for the public release list. Turn the daily check off
with **Check GitHub automatically for new versions** when you prefer no
outbound calls.

**Export logs** writes the live 1000-entry stream plus the warnings and errors
kept outside it, so a long run does not lose the errors that matter. The database
section exports a zip snapshot; importing one stages it, and the next start swaps
it in and parks the previous database next to it under a name that ends in
`.preimport.bak`. Work you did after the export is not in the imported copy, and
the parked file keeps it. A staged import that you did not mean to make can be
discarded before the restart.

## The dashboard

The dashboard is the at-a-glance page. A red **Needs attention** band lists
everything that still wants a decision, with a count and a link per category:
failed downloads, hardlink decisions, quality reviews, archive copies, plugin
logins, failed sync jobs and partial playlists. When there is nothing to do, a
short all-clear replaces the band. **Review all** opens the
[Needs attention page](/reference#the-needs-attention-page).

Four metric tiles follow: playlists, downloaded tracks, library size and the
total number of open attention items. The activity panel shows live download
runs and the last sync runs with how many tracks each resolved. The connections
panel groups accounts, media servers and Cloudflare. A Spotify **web session**
row appears when the fallback cookie or free-account import is connected, even
when no official OAuth account is linked. An **Apple Music** row appears when
the Apple web session is connected.

The playlist section shows compact cards with a progress bar and an attention
count per playlist. On a fresh install, the setup checklist includes a
**Connect a lossless source** card. It links to the Qobuz, Deezer and Tidal
cards for the three token flows, and to Jumo and Monochrome, which need no
account.

The sidebar **download chip** appears while a playlist download is running or
paused: how many runs, how many tracks are done, and how many failed. Click it
to open the live downloads on the Downloads page.

## The Playlists page

Saved playlists show as cards or compact rows; the switch sits above the list
and the choice is remembered. In card view, **Small**, **Medium** and **Large**
set the card width, from a six-column overview to wide covers, and the size is
remembered too. The sort control offers **Recently synced**, **Name A-Z** and
**Most tracks**, also remembered for the next visit. Pinned playlists stay above
the chosen order.

**Needs attention** narrows the list to playlists with a failed source sync, a
partial snapshot, failed track downloads, or downloaded tracks waiting for a
quality or dedup decision. The count on the chip says how many there are, and
the cards name the part that needs a look. A playlist with regular pending
downloads is not flagged.

**Select** turns the list into selection mode: tick several playlists, then
**Refresh**, **Download missing**, **Export CSV** or **Remove**. A bulk export
of more than one playlist is a single zip with one CSV per playlist, because
browsers block a burst of downloads. A bulk remove deletes the playlists and
their track lists but leaves the music files on disk. File imports cannot
refresh; re-import their file instead.

Each card and row has a **...** menu with the same actions for one playlist.
**Pin to top** keeps a playlist above the others until you unpin it. **Remove**
offers the usual choice to delete the downloaded files too. Next to the menu,
the **Publish** button creates sync jobs for several playlists at once with one
schedule and one set of targets. See
[Publishing playlists](/publishing#publish-playlists-in-a-few-clicks).

## Playlist options

Open a playlist card and one compact setup panel holds everything specific to
it. The header keeps the live counts (tracks, downloaded, failed, attention)
next to the save state, so the page does not need a second status card. The
panel splits into a downloads column and an automation column, with the archive
destinations as one checklist:

| Option | Notes |
|---|---|
| Name | Shown everywhere in the app. The playlist's folder is named after it (or after the folder you type) |
| Download folder | Empty means the default music path, with a subfolder named after the playlist. A folder typed here is used as it is |
| Audio quality | One of the presets above, or advanced filters |
| Strict quality | Shown when a quality preset is selected. On: only the exact quality is accepted. Off: the download steps down to the best lower quality the enabled plugins declare. See [strict quality](/concepts#strict-quality) |
| Allow container changes | Shown when strict is off: whether a fallback step may leave the chosen container |
| Archive destinations | A checklist of the default archive and every enabled profile. Select as many as this playlist should write. Nothing selected means no archive copies. **Manage profiles** opens the archive settings; the folder and format belong to the profile, not the playlist |
| Auto-sync schedule | Manual, hourly, daily, weekly, monthly or cron. A refresh that fails is retried at the next interval, not on every check, so a broken source cannot hammer itself |
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
sample rate, format and file size. The list shows 100 tracks per page; the pager
below it moves through the rest, so a playlist with thousands of tracks opens
quickly. Every column header sorts the list; click it again to reverse the
order. Next to the status pill sits a small archive check:
a green check when this track's archive copy is on disk, a quiet dash when the
track has no copy yet, and a warning when the recorded copy has vanished. With
no archive destination selected for the playlist the check stays away. Click the
check to open the archive dialog: it shows the copy's state, repeats any
archive failure in full, and offers **Create archive copy** or **Recreate
archive copy**, which rebuilds the file from the current download. A failed
rebuild leaves the previous copy in place and keeps it on the row, so
deleting the playlist files still removes it. The **!** pill beside the
status opens the same dialog, so a failed copy is one click from being
repaired. The check and the dialog cover the default archive; extra
profile copies are repaired from the
[Needs attention page](/reference#the-needs-attention-page). Enable **Show the
file column on playlist tracks** on the Advanced page to see the file names,
including `archive: <name>` chips for archive copies.

- The filter box narrows the list; **Needs attention** shows only failed tracks
  and tracks waiting for a dedup or quality decision.
- The table shows 100 rows per page, with **First**, **Previous**, **Next** and
  **Last** under it and a `Showing 1-100 of 2400` line above the buttons. Sorting
  and filters apply to every track, not only the visible page.
- **Choose source** opens the manual picker: every enabled plugin's search
  results for that track, best guess first, each with the plugin name and a
  likely/unsure pill. The track's own length sits at the top and every hit
  shows its length and the difference, so a preview or a longer edit stands
  out before the click. Every hit also carries its quality: a catalog that
  knows the track's ceiling reports the lower of that ceiling and your
  configured tier (Qobuz), the yt-dlp-backed plugins probe the selected
  stream before anything downloads (codec, bitrate, sample rate), and for
  the rest the row names what the plugin declares it can deliver. A pill
  compares that with the playlist's
  target: **matches target**, **saved as-is** when the plugin cannot deliver
  the target and the pick keeps its source quality, **outside target** when a
  source sits above a cap and keeps its quality, or **off target** when the
  plugin will convert the pick to the requested container. A band is only
  called a match when the probe measured it. Providers search at the same time
  and results appear as they arrive; the pick itself shows a progress bar and
  a running timer. Use it when the automatic match picked the wrong recording
  or found nothing; the pick downloads through the normal pipeline and
  replaces an existing file.
  A source that fails stays in the list with the reason, ready for another
  pick. The automatic search refuses results it cannot vouch for, so a wrong
  song is a miss you repair here instead of a silent download.
  The picker filters by plugin, match confidence and **can serve target**, and
  each row can copy its source URL or open that plugin's settings. The filters
  and scroll position are remembered when you close and reopen it. An empty
  result list names the plugins that could not be searched, with the reason
  (down, timed out, search failed or login needed), and links straight to
  their cards, so a short list never looks like a silent failure.
- A flagged track row (wrong length or inflated bitrate) shows a **!** pill and
  two buttons: **Keep** accepts the file as it is, **Retry** downloads it again
  while skipping the provider that delivered the flagged file. The file stays
  in the library until you decide. See
  [the quality review](/concepts#the-quality-review).
- **Remove** opens three choices for a downloaded track: remove it from the
  playlist but keep the file, delete the file and its archive copy but keep the
  track (it goes back to pending for a later download, handy after a wrong
  match), or delete the file and remove the track. Tracks without a file leave
  right away.
- **Export CSV** downloads the whole track list as a spreadsheet.
- **Refresh** fetches the playlist again and applies the sync mode. A refresh
  waits while that playlist has a download run, because it can add or remove
  rows under the run.
- **Download missing** starts a download run. While one is live, the button is
  replaced by **Pause**, **Resume** and **Cancel**.
- When tracks are failed, **Retry failed (N)** retries all of them in the normal
  waterfall order and **Retry elsewhere** also leaves out the plugin that failed
  each track last time. A progress card shows the batch; the final toast says
  how many downloaded and what still needs attention: failed, waiting for a
  review, already downloading in another run, or no longer there. A
  failed-download toast can link back with **Retry** and start that one track
  again.
- **Remove playlist** removes the playlist, with an option to delete the
  downloaded files too.

<img src="/screens/source-picker.webp" alt="Choose source dialog with filters, exact quality facts and target match pills" width="800">

## The Needs attention page

<img src="/screens/attention.webp" alt="Needs attention page with the filter bar, the repair summary and the failed group" width="800">

**Needs attention** in the sidebar collects every track and playlist that still
wants a decision, grouped by the kind of problem rather than by playlist. The
metric row is the filter bar. **All** shows only the categories; pick
**Failed**, **Reviews**, **Hardlinks**, **Archives** or **Playlists** to see that
group alone. A group with more than eight rows shows the first eight and a
**Show all** button.

The page opens in **Automatic** mode. The **Fix what can be fixed** card says
what the fixer will do, for example *retry 9 downloads, keep 2 length-reviewed
files, retry 2 quality reviews, recheck 1 unverified file, hardlink 2
duplicates, rebuild 2 archive copies and re-sync 1 playlist*. **Fix all**
applies the matching repair to every group. Each group also has its own
**Fix all** button. The repair runs as a background job: leave the page and it
keeps going, and the Downloads page shows it in the active runs list with its
queue count and ETA. A progress bar and a **Stop** button stay visible while the
work runs.

Simple mode shows one summary card with the same **Fix all** button and a note
that the per-track controls live in Advanced mode.

The mapping is deliberate: failed downloads retry through the normal waterfall,
length reviews keep the playable file, quality reviews fetch a new copy,
duplicates hardlink the existing copy, archive warnings rebuild the archive
copy, missing files retry the download, unverified files re-run the audio
check, and playlist syncs refresh the source.

Choose **Review individually** to show the old per-row controls: checkboxes,
the bulk action bar, and the action buttons for one track or one group. The
choice is not destructive; switching back to Automatic simply hides the
individual controls.

Each row carries the track, artist, playlist link, the original reason and, in
individual mode, the actions that make sense for it:

| Group | Actions |
|---|---|
| No source, wrong format, wrong length, not audio, corrupt, login, timeout, cancelled, metadata, other failures | **Retry**, **Elsewhere** (skips the provider that failed last), **Hardlink** (uses a file another playlist already has), **Open** |
| Hardlink decisions | **Hardlink existing**, **Download new**, **Open** |
| Length and quality reviews | **Keep file**, **Retry download**, **Open** |
| Archive copies | **Recreate**, **Open** |
| Missing files | **Retry**, **Elsewhere**, **Hardlink**, **Open** |
| Unverified files | **Recheck**, **Open** |
| Playlist syncs | **Re-sync**, **Open** |

Tick several rows in individual mode to get one bulk bar with **Retry
downloads**, **Retry elsewhere**, **Find hardlinks**, **Recheck files** and
**Recreate archives**, depending on the kinds you selected.
The page reports what changed and what still needs attention. It refreshes itself
when the store changes, so a run started elsewhere appears without a manual
reload. The dashboard's attention band links straight into the matching group.

## The Downloads page

<img src="/screens/downloads.webp" alt="Downloads page with live playlist runs and recent track activity" width="800">

The Downloads page is the live view of the download engine. It refreshes every
second while it is open.

- **Playlist runs** lists every run that is running, paused or cancelling. Each
  card shows tracks done, failed and queued, an expected finish time, the track
  and plugin it is on, and buttons for **Pause**, **Resume**, **Cancel** and
  **Open playlist**. Several tracks of a playlist download in parallel; the
  limit is on the Advanced page. Every track is attempted once per run, so one
  broken track cannot stall the rest.
- **Track activity** lists recent track downloads from the last 30 minutes,
  including the plugin, the status and the saved path. A failed entry keeps the
  reason, and a pick that fails never stays on "downloading".
- The cards at the top count the runs in progress, the tracks downloading now,
  and what finished or failed in the last 30 minutes.
- **Download a song** opens the manual download page described below.

### Download a song

<img src="/screens/download.webp" alt="Download a song page with source search, link fields and quality controls" width="800">

Open it with **Download a song** on the Downloads page. There are two ways to use
it:

- **Search every source** takes `Artist - Title` (the title alone also works)
  and searches every enabled plugin at once through the same source picker as a
  playlist row. Results stream in with the plugin, match confidence, duration
  and quality. Nothing downloads until you pick a result. A successful pick
  closes the picker and saves the file; a failed pick stays open with the
  reason. The picker keeps its filters while it stays open.
- **Paste a link** accepts a YouTube, SoundCloud, Bandcamp or other URL that an
  enabled plugin can handle. Artist and title are optional; when you leave them
  empty, MelodyBridge reads them from the source. The plugin delivers the file,
  then MelodyBridge writes the tags and stamps the `MELODY_ID`.

**Save to** defaults to the music path and accepts any absolute folder. The file
is named by the **Downloaded file names** pattern on the Advanced page (default
`Artist - Title`), keeps its source tags, and joins the library on the next
scan. The saved card shows the path, the plugin that delivered it and any
warning, with **Copy path** and **Open Library** actions. A container that
cannot carry the `MELODY_ID` tag shows a warning on that card, because the
scanner matches on that tag and would otherwise leave the file out.

The quality picker applies to search picks. A direct link downloads the source's
own stream, then tags and verifies the file.

## The Plugins page

<img src="/screens/plugins.webp" alt="Plugins page with compact source cards and routing kept in an advanced panel" width="800">

Providers search for your tracks and plugins download them. The page has two
jobs: set a source up, and decide which source is tried first.

**Routing and tools** opens the advanced panel only when you need it. It holds
the source order, the quality preview, the yt-dlp version and update action, and
the search routing map. The order is the whole story for the **No filter**
quality. When a playlist asks for a specific quality, MelodyBridge first picks
the plugins that can really deliver it (their declared capabilities decide) and
this order breaks ties between plugins of the same kind. Pick a preset in
**Preview quality** to see that capability order and which sources it skips;
**No filter** keeps the preview out of the way because it is the same as the
list above.

The setup cards are split into **Your subscriptions** and **Free and community
sources**. Qobuz, Deezer and Tidal each get an account card with **Account and
settings**. Everything else needs no subscription; a companion tool or container
(yt-dlp, slskd, FlareSolverr for Lucida) is named on the card. The search box and
the **All**, **Accounts**, **Free** and **Problems** filters narrow the cards,
and the count says how many each filter would show. **Problems** includes
unavailable sources and stored logins that stopped working. The **Cards** and
**Rows** switch changes the layout and remembers the choice. Simple mode starts
in Rows; Advanced starts in Cards. Both keep the same filters, routing panel,
quality badges and setup controls.

The **Search routing** panel inside **Routing and tools** shows where each
source gets its track. A provider row names the data
it reads (ISRC, title and artist) and the source cards it can search. Each
provider carries a tier: **official**, **ripper**, **unofficial** or **self**.
When two providers target the same source, the card marks the one the router
uses as **primary**. Lucida has no searcher of its own; it appears as a target
of the official providers. The **self** tier covers the five sources that
search their own catalogue by title: Soulseek, SoundCloud, Internet Archive,
yt-dlp and FLAC Music. Each one has its own provider, so every built-in source
is provider-backed. A source marked **fallback** only runs after every other
source. A direct-URL source has no search at all. If a source declares a
provider search but no provider targets it, the card calls that out in red.
Third-party sources that still search inside the downloader are listed
separately under **Third-party sources that search themselves**.

<img src="/screens/plugins-routing.webp" alt="Expanded search routing panel with the ten providers and their tiers" width="800">

- Each plugin card shows its capability badges (formats, bitrate range) and a
  **Settings** row when it has options. The row opens a panel with the fields
  that matter (a token, an API key, a quality) and hides the rarely touched
  knobs (commands, custom folders) behind **Advanced**. Secret fields render
  as password inputs with a reveal button, and values are remembered per
  plugin.
- A working login shows what it actually gives you: the Qobuz account and
  configured tier ("Studio, hi-res up to 96 kHz"), the Deezer plan ("No
  lossless plan"), or the Tidal country, tier and expiry. The card also says
  when it was last checked, and its refresh button tests only that plugin.
  Plugins that have a docs page carry a **Setup guide** link.
- An unavailable plugin (missing binary, dead service) is skipped by the
  waterfall. The card says what is missing, in one line, and **Refresh status**
  re-checks everything.
- A saved login that stopped working gets a red mark: a red exclamation on the
  plugin icon, a red card border, and a status pill naming the problem (token
  expired, token rejected, account blocked, plan restricted). A banner at the top
  of the page lists every affected plugin with an **Open settings** button, the
  Plugins link in the sidebar carries the same mark, and a toast names the
  plugin when the problem is found. A login that was never set up is not a red
  alert: the card only says what to add. **Dismiss** hides the banner until a
  login problem appears or changes, and the mark on the card stays.
- A failing card has **Copy diagnostics**: version, plugin id, enabled state,
  login state, last reason, declared capabilities and the settings with secrets
  masked. Paste it into a bug report.
- **Save settings** tests the new token or ARL immediately instead of reusing
  the previous "unavailable" answer. Status probes run in parallel, so one slow
  provider does not hold up the rest; each card shows a checking state while its
  probe runs.
- Live playlist runs and recent track activity are on the
  [Downloads page](/reference#the-downloads-page), so this page stays focused on
  plugin setup and status.

## The Library page

<img src="/screens/library.webp" alt="Library page with scan folders and the track identity panel" width="800">

The Library page covers music you already own. **Add location** points
MelodyBridge at a folder; new playlists register their download folder here
automatically, and so does every archive root you save.

- Each location has its own schedule (manual, hourly, daily, weekly, monthly or
  cron) and an optional **Live monitoring** toggle that rescans when files
  change. **Run scan** does all folders immediately.
- The cards at the top show how many files carry a `MELODY_ID`, the total size
  of the main files and archive copies, the number and size of the archive
  copies, and how many scan folders are archive folders.
- The scanner reads tags, not filenames, and matches files to playlist tracks by
  their `MELODY_ID` tag, so a song you already have is never downloaded again.
  Location cards show their own file count and folder size, the schedule, the
  last scan, and a **path missing** pill when the folder went away. A folder
  that sits in a configured archive path is marked **archive folder**: its files
  are copies and are never adopted as a track's main file.
- Sizes come from the database: the app knows how big a file is when it writes
  it, so the page does not measure the library again on every visit. A file
  from before that was known is measured once, in the background, and the
  answer is kept. Archive copies are counted where they live, so an archive
  folder reports the real number and size of its files.
- The **Track identity** panel on the right repeats the short version of how
  matching works. The long version is in [How it works](/concepts).

## The Advanced page <Badge type="warning" text="Advanced" />

<img src="/screens/advanced.webp" alt="Advanced page with download, display, logging and maintenance options" width="800">

The knobs most people never need, in five panels:

- **Advanced mode**: *Show advanced options and technical details* reveals the
  longer notes and extra fields on the settings and import screens. The setting
  is stored and survives a restart. Leave it off for the everyday interface.
- **Downloads**: *Max simultaneous track downloads* (1 to 8, default 2; the
  number counts every playlist and manual download together, and the next run
  picks up a change), *Plugin time budget* and *Track time budget* (in seconds:
  how long one plugin and one whole track may take before the waterfall moves on
  or gives up), *Downloaded file names* (below), *Hardlink duplicate tracks
  across playlists*, the dedup toggle described in [How it works](/concepts),
  and *Prefer official sources before community mirrors*. That last switch is
  on by default: official Qobuz, Tidal and Deezer targets run first, then
  Lucida on their URL, then Jumo and Monochrome, then the self-search sources.
  Turn it off to follow the source order exactly.
  **Downloaded file names** is the pattern for new downloads: `{artist}`,
  `{title}`, `{album}` and `{track}` are filled per track, and the default
  `{artist} - {title}` puts the artist first. Use `{source}` to keep the name the
  plugin chose. The pattern does not rename files that are already on disk; the
  maintenance action below does that.
- **Display**: *Show the file column on playlist tracks*, which reveals the
  exact filename behind every track. Handy when hunting inflated downloads.
- **Logging**: *Show database activity in the logs*. Off by default, because
  EF Core writes a line for every SQL command. Warnings and errors show up
  either way. Applies immediately.
- **Maintenance**: **Recompute audio facts** reads bitrate, sample rate and size
  straight from the files on disk, for downloads that predate the probing, and
  re-runs the spectrum check on what it reads, so an inflated file or a lossy
  source boxed as FLAC is flagged after the fact. Tracks whose file has vanished
  are queued for a re-download. A spectrum review you already settled with Keep
  stays settled; the check does not reopen it for the same file.
  **Re-tag library** fills what a downloaded file is missing: cover art from the
  source, lyrics, the release credit and the album name. Only missing fields are
  written, so artwork a plugin embedded is left alone. It reports how many files
  changed, how many were already complete and how many could not be read.
  **File links** adopts files that are already in a playlist folder, including
  old `mb-` MELODY_IDs and untagged files whose artist, title and length match
  a row; the tag is rewritten to the deterministic id. **Check duplicate
  files** lists numbered copies of the same track. The file a row points at is
  kept, unreferenced copies are removed, and a `(2)` keeper takes the plain
  name back when the plain copy is the orphan. A group no row owns is reported
  but left untouched.
  **Check file names** applies the saved file name pattern to files already
  downloaded, archive copies included. The preview breaks the library down:
  how many tracks would be renamed, how many already follow the pattern,
  how many files are missing, how many paths are shared by another row, how
  many are outside the managed folders, and how many archive copies would be
  skipped. Every skipped track is logged with its path and reason at debug
  level. A path shared by several rows for the same song moves once and every
  row follows; a path shared by different songs stays put. Files outside the
  configured music and archive folders and files that cannot be renamed are
  left alone, and the result says how many moved, were skipped or failed. Hard
  links keep working: renaming one playlist's name leaves the other name on the
  same bytes. Playlists and sync jobs pick the new paths up on their next run.

## The Logs page

<img src="/screens/logs.webp" alt="Logs page with the filterable event stream" width="800">

The event stream shows playlist syncs, downloads, scans and errors. When there
are errors, a banner at the top lists them; click one to jump to that area.

- Filter by area (Playlists, Downloads, Sources, Library, Sync, Database,
  System), level (Info+, Warning, Error, Debug) or source (Qobuz, Tidal, Deezer,
  yt-dlp, SoundCloud, Monochrome, Jumo, Lucida, Archive.org, Soulseek,
  Waterfall, Other). Every area chip shows how many entries it holds, and the
  source list shows its counts too. The search box also matches the friendly
  area names.
- The page opens on **Info+**, so debug and trace noise stays out until you ask
  for it. Framework chatter from ASP.NET, the host and `System.*` loggers is
  kept out as well; their warnings and errors still appear.
- **Pause live** stops the two-second refresh so the stream stays still while
  you read it; resuming catches up at once. **Group by track** puts one
  download attempt's search, skip reasons, download and verification under the
  same track header.
- The stream renders the newest 200 matching entries. **Show 200 more entries**
  appends the next page. The filters and counts cover the live buffer. **Copy**
  and **Export** also carry warnings and errors that already rolled out of it,
  so a long run keeps its full problem history.
- Each row has **Copy** and **Pin**. A pinned entry moves to the pinned card
  above the stream and stays there while it updates; **Clear** empties the
  pinned list. **Show only problems** narrows everything down to errors.
  **Copy** puts the filtered entries on your clipboard, **Export** writes them
  to a file, and **Clear** empties the buffer.
- The filters, search text, order and grouping are remembered when you leave
  the page and come back. A restart starts clean.

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
| *Qobuz: Qobuz blocked this account (USER_BLOCKED)* | The token is valid, but Qobuz refuses downloads for this account. Use another account or ask Qobuz support |
| *Tidal: The Tidal token expired* / *Tidal rejected the session* | Paste a fresh token on the Plugins page, or run `tiddl auth login` |
| *Rhythmax has no working Tidal account right now* / *only has preview accounts* | The public Rhythmax pool is between accounts or serving previews. Retry later, or use your own Tidal session with the tiddl source |
| *Rhythmax has no playback slot right now (Tidal 429)* | The public service rate limited the request. Retry later; the waterfall can use another source |
| *flac.music.hi.cn did not answer* / *no longer lists this track* | The fallback site is down or the song id disappeared. Retry later or use another source |
| *flac.music.hi.cn served aac for the requested mp3 tier* | The 128 tier returned AAC instead of MP3. The waterfall tries the other platform's candidate; if none matches, choose a different quality or source |
| *Deezer: The Deezer ARL is no longer signed in* | Copy a fresh `arl` cookie from deezer.com and paste it on the Plugins page |
| *no other provider is available: X delivered the flagged file and was skipped* | You chose Retry and X was the only enabled plugin left, so there was nothing else to try. Enable another plugin; a later Download attempt may use X again |
| *corrupt download: ...* | The bytes could not be parsed at all, so the file was deleted and the track marked failed; the next run retries |
| *could not verify the download: ffprobe failed* | The file is kept; ffprobe could not run (a missing tool, a timeout, or a resource limit). Retry the check, or install ffprobe, and the warning clears |
| *archive copy failed: ffmpeg not found on this host* | The download itself is fine; install ffmpeg if you want archive copies |
| *archive copy unverified: ...* | The copy is on disk but ffprobe could not check it. The next archive pass checks it again; a real defect becomes a normal archive failure |

A **needs review** state is not a failure either: it means two playlists
disagree about a track's quality and you get to pick, see
[How it works](/concepts). The [Needs attention
page](/reference#the-needs-attention-page) groups every one of these messages
and lets you retry a whole group, find a hardlink, or rebuild archive copies
without opening each playlist.

One more place to look: the **Connections** panel on the dashboard shows the
state of Spotify, YouTube, Apple Music, your media servers and FlareSolverr at
a glance. A
media server profile that exists but is not usable says **not configured**, not
ready. A connected server names the account and host it reached, and a server
that did not answer says **unreachable** with the reason. While a server is
still being asked it says **checking**, and the panel updates itself when the
answer lands: the dashboard never waits for a media server that does not
answer.
