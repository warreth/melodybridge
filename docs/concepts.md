# How it works

MelodyBridge does three things: it fetches playlists, it downloads and verifies
the tracks, and it publishes playlists to your players. This page explains what
happens under the hood, because the knobs make more sense once you know the
moving parts.

## From link to library

The short version of the pipeline:

1. You save a playlist. MelodyBridge fetches the track list once and stores it
   in a local SQLite database.
2. **Download missing** sends each pending track down the plugin waterfall. The
   first source that returns a file which passes your quality bar wins.
3. Every produced file is checked, tagged with a source-derived ID, and written
   to the playlist's download folder.
4. Library scans and the reconciler later match files back to playlist rows by
   that ID tag, so the database never has to be right, only helpful.

## Playlists are snapshots

When you add a public Spotify or YouTube link, MelodyBridge reads the playlist
(page scraping for Spotify, yt-dlp for YouTube) and saves the whole track list
locally. No API key or account is needed for public playlists. You also get the
cover art, the track count and each track's duration.

From then on the local snapshot is what the app works from. Pressing
**Refresh**, or letting the **Auto-sync schedule** fire, fetches the source
again and merges the result. New tracks show up as pending; disappeared tracks
follow the **Sync mode** you picked:

- **Additive** keeps removed tracks as history. Nothing vanishes from your
  library.
- **Mirror** removes tracks that are gone from the source, so the published
  playlist matches the source exactly.

Two things worth knowing:

- Auto-sync refreshes the track list. Downloads stay manual unless the playlist
  opts in with **Download new tracks after auto-sync**: after a successful
  refresh, MelodyBridge starts a download run as soon as there is pending work,
  new tracks and earlier failures alike. Failed tracks are retried every
  interval. The toggle is off by default and hidden for file imports, which
  have no live source. Sync jobs publish whatever files exist at their run time.
- If a fetch comes back incomplete, the playlist card shows a **partial sync**
  warning and the next refresh fills the gaps.

Private playlists, collaborative playlists and your liked songs need an
account connection or a file import. See [Accounts and imports](/accounts).

## The waterfall

Every download runs through a waterfall of plugins. MelodyBridge does not throw
every plugin at every track: it routes first, then runs the survivors in order.

### Routing

Each plugin declares its honest capabilities: the container formats it can
produce, which bitrate levels those arrive in, whether lossless content is
really in reach, and whether the plugin can be *asked* for a level or merely
hands back whatever the source serves. Two things use that list:

- **Format and bitrate filters.** A plugin that cannot serve your requested
  quality is skipped before any network call. When every plugin is out, the
  track fails with a message like *your quality filters excluded every plugin*,
  which is your cue to loosen the filter or enable more plugins.
- **Run order.** With **No filter** your own order on the Plugins page is the
  order. When you ask for a specific quality, the plugins that can actually be
  asked for it run first, and your order decides between plugins of the same
  kind. A Lossless request therefore starts at the lossless producers and an
  mp3 request at the plugins that can hand out mp3, without any per-quality
  configuration.

### Strict quality

Every playlist has a **Strict quality** switch. On, which is the default, only
the quality you asked for may be delivered; when nothing can serve it, the
track fails with the reason. Off, the download steps down through the quality
levels the enabled plugins themselves declare, best first, until one delivers:
a Lossless request lands on the best lossy file, and a request with a bitrate
floor drops its floor instead of failing. The follow-up switch **Allow
container changes** decides whether a fallback step may leave the chosen
container; turn it off when the container itself is the point.

Plugins that are missing their tool or reporting a dead service mark themselves
unavailable and get skipped automatically. The pill on their card shows the
current state, and **Refresh status** re-checks them.

MelodyBridge also watches how each plugin behaves over its recent attempts. A
plugin whose last three attempts all failed is skipped for five minutes instead
of costing every track a timeout, and a plugin that mostly fails lately runs
after the healthy ones. Two time budgets keep a run moving: one plugin may
spend at most four minutes on a single track, and a whole track gets twenty
minutes across every plugin and quality step. Both are adjustable on the
Advanced page.

### The plugins

| Plugin | What it fetches |
|---|---|
| Lucida | High quality rips from lucida.to (Tidal, Deezer, Amazon Music, Qobuz). Needs a Cloudflare solver, see [Lucida and FlareSolverr](/lucida) |
| Monochrome | Lossless FLAC from the official Monochrome API, no account needed |
| Soulseek | Lossless and high-bitrate files from Soulseek peers, through your own slskd instance. See [Soulseek and slskd](/slskd) |
| Qobuz | Lossless and hi-res files from your own Qobuz subscription, through qobuz-dlp. See [Qobuz and qobuz-dlp](/qobuz) |
| Deezer | MP3 or FLAC from your own Deezer account, through the deemix CLI. See [Deezer and deemix](/deemix) |
| DoubleDouble | Retired: doubledouble.top now demands a captcha on every submission, so the plugin reports itself unavailable. It stays in the list (last) so an existing install can turn it off |
| SoundCloud | Original uploads by the artist, usually 128 kbps or better |
| Internet Archive | Public recordings and digitizations from archive.org |
| yt-dlp | YouTube: audio-only best stream, top-5 ranked by match and duration |

### Finding the right recording

Matching uses more than the title. Search results are ranked on title and artist
similarity with a penalty for duration drift, so a live version or a radio edit
loses against the studio track. Candidates that carry an unrequested modifier
(karaoke, cover, remix, live cut and the like) are dropped before they can win,
and the playlist's own duration is re-checked against the hit before any bytes
are downloaded.

The strongest signal is the ISRC, the recording's international audio
fingerprint. Spotify accounts provide it for their tracks; for everything else
MelodyBridge looks it up through Deezer's public search and only accepts a
confident, duration-plausible match. Sources that index ISRCs (the Qobuz-backed
ones) search by that code first, which is how messy titles still land on the
exact recording.

<img src="/screens/plugins.webp" alt="Plugins page with the waterfall order, capability badges and live downloads" width="800">

## Quality checks

### The band is a filter, not a suggestion

When you pick a quality preset (or an advanced container and bitrate band),
MelodyBridge enforces it twice: first against the bitrate each search hit
reports, and again by probing the real file after download. A file outside the
band is rejected and deleted, and the waterfall moves on. Unknown bitrates pass
the first check and get decided by the measurement.

MelodyBridge does not re-encode the primary download. If no source can serve the
container you asked for, the track fails with a message saying so; that is the
filter working, not a bug. Archive copies are the one exception, and those are
converted after the fact from the file you already have.

### Real files only

A track counts as downloaded only after it survives three checks:

1. The file's bytes are sniffed for a real audio signature. An HTML error page
   or a JSON body served with a 200 status is deleted, and the waterfall
   continues.
2. ffprobe parses the file. The duration must roughly match what the playlist
   promised; the same check runs on search hits before a download even starts,
   so previews and wrong edits are usually stopped earlier.
3. The measured bitrate must land inside your band.

Anything corrupt is deleted and marked failed with the reason, and the next run
retries it. Nothing half-broken is left in your library.

### The spectrum check

Some sites hand out files that claim 320 kbps but hold far less. The spectrum
check does not trust the header: it decodes a window of the audio, averages its
spectrum, and looks at how the high end falls off. It only flags a file when
the spectrum drops off a steep codec-style corner well below what the declared
bitrate needs.

Set the mode under **Settings, Quality, Spectrum check**:

| Mode | What it does |
|---|---|
| Off | Trust the file |
| Fast | Analyze the first minute (the default) |
| Thorough | Analyze the whole file (the first 15 minutes of very long files) |

The check only warns, it never deletes. Track rows get an **!** pill with the
reason, and a playlist whose downloads look inflated shows a hint box counting
them. It needs ffmpeg and ffprobe on the host; without either, files simply
skip the check. It stays honest about what a spectrum can and cannot prove: a
high end that fades out gently is music that was mixed that way, not a
mislabelled rip, and Opus files stay near full band even at low bitrates, so a
dark mix or an Opus file is never called inflated and never gets a quality
hint. Only a steep codec corner, well below what the declared bitrate needs,
counts.

<img src="/screens/playlist-details.webp" alt="Playlist details page with track quality columns and a download in progress" width="800">

## Track identity

Each downloaded file carries a `MELODY_ID` tag. The value is derived from the
source, so it is the same on every machine and in every database:

| Source | ID format |
|---|---|
| Spotify track | `spotify:{trackId}` |
| YouTube video | `yt:{videoId}` |
| Internet Archive item | `ia:{identifier}` |
| CSV import row | `csv:{hash}` of artist, title and duration |
| No platform ID | `mbh:{hash}` of the same fields |

The tag is written after the file passes the quality bar, and it lives where the
container format keeps its comments: an ID3v2 TXXX frame for MP3, a Xiph/Vorbis
comment for FLAC, Ogg and Opus, and a `MELODY_ID=` marker in the comment field
for MP4 and the rest.

## Surviving a database wipe

The database is a cache. The files on disk are the real library, and their ID
tags are the index. That means you can throw the database away without losing
anything:

1. Stop the app (`docker compose down`).
2. Delete the database, `./data/app/melodybridge.db` (plus the `-wal` and `-shm`
   files if they are there).
3. Start the app again and re-add the same playlists, using the same links.
4. Run a **Library scan**, or just restart. The reconciler matches every file on
   disk to its playlist row by `MELODY_ID`, and nothing downloads a second time.

Download folders register themselves as scan locations, and the reconciler runs
at startup and after every scan, so a fresh database over an existing music
folder heals by itself. Playlists imported from a CSV file or your liked songs
need the import repeated before their rows exist again.

## Duplicate tracks <Badge type="tip" text="Optional" />

The same track often sits in more than one playlist. Downloading identical bytes
again would waste disk space, so before a download MelodyBridge checks whether
the same `MELODY_ID` is already sitting in another playlist folder:

- **Same quality.** The existing file gets a hard link into this playlist's
  folder: a second name for the same bytes, costing zero extra space. Both
  folders stay normal folders with normal files in them.
- **Different quality.** Nothing is guessed. The track pauses as **needs
  review** with two buttons: **Download new** fetches a fresh file in this
  playlist's quality, **Hardlink existing** accepts the other file as-is.
- **Different drive.** A hard link only works within one filesystem. When the
  folders sit on different drives the track simply downloads the normal way; a
  copy pretending to be a link is never used.

Removing a track from one playlist removes only that playlist's name for it.
The bytes stay as long as any other playlist still links them. The whole feature
can be turned off with **Hardlink duplicate tracks across playlists** on the
Advanced page, and a track that was linked rather than downloaded does not
produce an archive copy, because no download happened.

## Archive copies <Badge type="tip" text="Optional" />

Archive copies give every download a second home. The use case is simple: keep a
FLAC archive for the long term, and let your media server stream smaller Opus or
AAC files. Or the other way around, your call.

Set an archive folder and format on the playlist page (**Archive copy**), or set
the defaults under **Settings, Default paths**. Leave the path empty and no
archive is made.

| Format | Result |
|---|---|
| Auto | A plain copy of the downloaded file |
| Opus | 128 kbps, small and efficient |
| FLAC | Lossless, level 5 compression |
| MP3 | 256 kbps, widely compatible |
| AAC | 192 kbps, lands as `.m4a` |

The track is fetched from the source once. The archive copy is produced locally
from that file with ffmpeg, so a second library never costs a second internet
download. If ffmpeg is missing, the download still succeeds and the track gets
an **archive copy failed** warning instead. The file column on the playlist
tracks (enable it on the Advanced page) shows an `archive: <filename>` chip on
tracks that have an archive copy.
