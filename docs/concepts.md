# How it works

MelodyBridge does three things: it fetches playlists, it downloads and verifies
the tracks, and it publishes playlists to your players. This page explains what
happens under the hood, because the knobs make more sense once you know the
moving parts.

## Simple and advanced mode

A first install asks how you want to use the app. **Simple** keeps the sidebar
to the everyday path: playlists, downloads, attention, sources, sync jobs and
settings. The setup checklist has two steps. The sync jobs page still shows
publish profiles, but the editor keeps the path rules under a fold and the bulk
selection bar out of the way. Its dashboard keeps the library counters and
playlists, the attention page becomes one summary and one fix button, and the
plugins page starts in compact rows. **Advanced** shows the library, diagnostics,
logs and every setting, and starts the plugins page as cards. Both modes use the
same plugin controls and can switch between rows and cards.

The choice is a starting point, not a lock. Switch any time from the mode switch
at the top of the sidebar or from Settings, Appearance. Direct links to a hidden
page still work, so a bookmark or an existing link never breaks.

## Profiles

Three kinds of profiles show up around publishing, and each one answers a
different question:

- A **media server connection** is one server: URL and credentials. It lives in
  Settings, Media servers.
- An **archive profile** is one local second copy in its own folder and format.
  Playlists choose which copies to build. It lives in Settings, Default paths.
- A **publish profile** is one destination for a sync job: an M3U file or one
  media server. It picks the connection, playlist name, source copy and path
  rules. A job ticks one or more publish profiles, so each destination keeps its
  own rules.

The source copy is optional. **Primary library** publishes the normal download.
An archive profile publishes that copy instead. This is how one playlist can
become a normal Jellyfin playlist and a separate FLAC playlist without the two
fighting over path rules.

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

When you add a public Spotify, YouTube, Apple Music, Tidal, Qobuz, Deezer,
SoundCloud or ListenBrainz link, MelodyBridge reads the playlist (page scraping
or the public API for Spotify, yt-dlp for YouTube and SoundCloud, the public
Apple Music API with a page fallback, Tidal's public catalogue token, Qobuz's
token-backed API, and the public Deezer and ListenBrainz APIs for those) and
saves the whole track list locally. Most public playlists need no account;
Qobuz playlists use the Qobuz token from the plugin. You also get the cover art,
the track count and each track's duration.

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
  configuration. A source marked as a fallback stays last whatever quality
  you ask for.

### Search providers

A source does not have to know how to search. A **search provider** turns a
track query into a download URL, and the source only downloads that URL. The
**Routing and tools** panel on the Plugins page shows the search routing map:
every provider, the sources it can search, and the data it reads.

The built-in Tidal ISRC searcher is the example. It asks Tidal for the ISRC
first, accepts an exact code as the recording even when the title is spelled
differently, and falls back to a title search. Both Tidal sources use it: the
tiddl source downloads through your own session, and Rhythmax downloads
through the public API. Neither one text-searches Tidal on its own.

Sources fall into four groups:

| Behaviour | What it means |
|---|---|
| Official catalogue | The provider reads an official ISRC index and returns the source's own URL. Tidal and Rhythmax share the public Tidal ISRC searcher; Qobuz uses your token; Deezer uses its public API |
| Official URL, community downloader | Lucida has no searcher. It consumes the Qobuz, Tidal or Deezer URL an official provider already resolved, and can also rip SoundCloud, Amazon and Yandex links |
| Unofficial catalogue | Jumo is a Qobuz front-end: it uses the same Qobuz album and track ids, so the Qobuz hit becomes a `jumo:` URL without a second search. Monochrome has its own snowflake ids and searches its catalogue by ISRC first |
| Self-search | A provider searches the source's own catalogue by title because the service has no ISRC index. Soulseek, SoundCloud, Internet Archive, yt-dlp and FLAC Music each have their own provider; the downloader only downloads the URL. The Chinese fallback searches by title only and stays last |

A provider target can carry a priority, a tier and a provider can carry its own
priority. Official targets run first, then the community downloader on their
URLs, then the unofficial catalogues, then the self-search providers. Inside a
tier the user's source order still decides. **Prefer official sources** on the
Advanced page turns this off and restores the pure source order. The Plugins
page shows the tier and any routing problem it finds, such as a target that
names no registered source.

The routing card also names the data each search reads: ISRC, title and
artist, a URL, or a duration. A source that is marked as a provider target
but has no registered provider is called out in red, because it cannot find
tracks until the provider is back.

### Strict quality

Every playlist with a quality preset has a **Strict quality** switch. On, which
is the default, only the quality you asked for may be delivered; when nothing
can serve it, the track fails with the reason. **No filter** has no bitrate or
container target, so the switch stays out of the way until you pick a preset. Off, the download steps down through the quality
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
| Lucida | High quality rips from lucida.to (Qobuz, Amazon Music, Deezer, Tidal). Needs a Cloudflare solver, see [Lucida and FlareSolverr](/lucida) |
| Monochrome | Lossless FLAC from the official Monochrome API, no account needed |
| Soulseek | Lossless and high-bitrate files from Soulseek peers, through your own slskd instance. See [Soulseek and slskd](/slskd) |
| Qobuz | Lossless and hi-res files from your own Qobuz subscription, through qobuz-dlp. See [Qobuz and qobuz-dlp](/qobuz) |
| Deezer | MP3 or FLAC from your own Deezer account, through the deemix CLI. See [Deezer and deemix](/deemix) |
| Tidal | Lossless and hi-res files from your own Tidal session, through tiddl. See [Tidal and tiddl](/tidal) |
| Tidal (Rhythmax) | Tidal FLAC and AAC through the public Rhythmax API, no Tidal account needed. See [Tidal and tiddl](/tidal#no-tidal-account-rhythmax) |
| Jumo | Qobuz rips through the community front-end jumo-dl.pages.dev, no account needed. See [Jumo (Qobuz rips)](/jumo) |
| SoundCloud | Original uploads by the artist, usually 128 kbps or better; the SoundCloud provider ranks the top five |
| Internet Archive | Public recordings and digitizations from archive.org; the archive provider scans every matching item |
| yt-dlp | YouTube: audio-only best stream; the YouTube provider ranks the top five by match and duration |
| FLAC Music (CN) | Kuwo and NetEase lossless search, title-only fallback. See [FLAC Music fallback](/flacmusic) |

### Finding the right recording

Matching uses more than the title. Search results are ranked on title and artist
similarity with a penalty for duration drift, so a live version or a radio edit
loses against the studio track. Candidates that carry an unrequested modifier
(karaoke, cover, remix, live cut, review and the like) are dropped before they
can win, a version marker the playlist asks for must be present in the hit
("(VIP)", "(Acoustic)"), and a hit that is not a plausible version of the track
at all is a miss: it is never downloaded just because it was the best of five.
The playlist's own duration is re-checked against the hit before any bytes are
downloaded.

The strongest signal is the ISRC, the recording's international audio
fingerprint. Spotify accounts provide it for their tracks, liked songs and
playlists alike; for everything else MelodyBridge looks it up through Deezer's
public search and only accepts a confident, duration-plausible match. Sources
that index ISRCs use it wherever they can: Qobuz, Deezer, Jumo and Monochrome
search by the code itself, and Qobuz, Deezer, Jumo, Monochrome and the Tidal
ISRC searcher accept a hit carrying the exact code as that recording, even
when its title is spelled differently. The Tidal searcher resolves the URL once
for both Tidal sources. Lucida no longer searches; it downloads the official
Qobuz, Tidal or Deezer URL. YouTube and SoundCloud do not index ISRCs at all,
so for those the ranking rules above are the whole gate. FLAC Music has no
ISRC index either; its title search is why it stays last.

When the automatic match is wrong or finds nothing, every track row has a
**Choose source** button: the manual picker lists what every enabled plugin
found, including the hits the automatic search refused, each with its plugin
name and a likely/unsure pill. The track's own length sits at the top and every
hit shows its length with the difference, so a preview or a longer edit stands
out. The providers search at the same time, results appear as each one answers
instead of waiting for the slowest, and a pick shows a progress bar and a
running timer while it downloads. Picking one downloads it through the normal
pipeline, files and archive copy included, and replaces whatever the row held
before; a source that fails stays in the list with the reason. Automatic
matching cannot be perfect on sources that carry no ISRC and no trustworthy
artist name; the picker is the honest way out of that. The same picker opens
from **Download a song**, where there is no existing row to replace.

<img src="/screens/plugins.webp" alt="Plugins page with compact source cards and routing kept in an advanced panel" width="800">

### Cover art and lyrics

A downloaded file gets what the source really knows. Spotify accounts (liked
songs included), Deezer playlists and ListenBrainz playlists carry a cover URL
and the album, so the artwork is embedded and the album tag written unless the
plugin already wrote its own. The album tag is what a media server groups on:
files without one fall back to their folder, which is why a playlist full of
YouTube rips shows up as one album. Lyrics come from LRCLIB, one lookup per
track, stored on the row and written as the lyrics tag. All of it is best
effort: a dead cover URL or a missed lyric leaves the file exactly as it was,
and none of it can fail a download. The Advanced page can fill the same fields
on files that were downloaded before this existed.

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

A hand-picked source in **Choose source** is the one place the filter steps
aside: you saw the hit and chose it. The picker shows what each candidate
really is before you click (see [the track list](/reference#the-track-list)),
and MelodyBridge never turns a lossy source into a lossless file. When the
plugin cannot deliver your target quality, the pick is saved at its source
quality and the row says "saved as-is"; when the plugin can serve your target
container, the pick is converted to it. The row says which one will happen.

### Real files only

A track first has to be real audio, and only genuinely broken files are thrown
away:

1. The file's bytes are sniffed for a real audio signature. An HTML error page
   or a JSON body served with a 200 status is deleted, and the waterfall
   continues.
2. ffprobe parses the file. A file it cannot parse (truncated mid-frame, junk
   under an audio header) is deleted and the track is marked failed, so a later
   run retries it. A wrong length is not a broken file: see the review below.
   When ffprobe itself fails (missing, a timeout, or a resource limit), the file
   is not judged: it stays and the row says it could not be verified.
3. The measured bitrate must land inside your band. A file outside the band is
   rejected and deleted, and the waterfall moves on.

The duration and band are also checked against search hits before any bytes are
spent, so previews and wrong edits are usually stopped earlier.

Nothing broken is left in your library. Files that are playable but doubtful
take the path below instead.

### The quality review

Two checks flag instead of delete. A file whose length differs from what the
playlist promised, and a file whose bandwidth says the declared bitrate is
inflated, both stay in the library. The track row gets a **!** pill explaining
the doubt and two buttons:

- **Keep** settles it: the file stays exactly as it is and the flag is cleared.
- **Retry** settles it the other way: the flagged file is deleted and
  downloaded again, with the provider that delivered it skipped for that
  attempt, so another source gets its turn. The skipped provider is only out
  of this one attempt; when nothing else can deliver, a later try may use it
  again.

A playlist with open flags shows a hint box counting them, and **Needs
attention** in the track filter lists flagged rows next to failed ones.

The windows are configured under **Settings, Quality**:

| Setting | Default | What it decides |
|---|---|---|
| Length tolerance (seconds) | 30 | The absolute part of the allowed length difference |
| Length tolerance (percent) | 10 | The relative part; the larger of the two wins |
| Spectrum strictness | Balanced (two classes below) | How far the measured bandwidth may sit below the declared bitrate before a file counts as inflated |

A 5:05 version of a 5:00 track passes with the defaults; a 5:45 extended mix
does not and waits for your call.

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

**Spectrum strictness** is the line in the sand: with the default, a 320 kbps
header over 192 kbps of real bandwidth is flagged and one class below is not.
An inflated file joins the quality review, so it stays on disk and you pick
Keep or Retry. A gentle low end that is not a codec corner gets an advisory
**quality hint** on the row instead, and there is nothing to decide.

Lossless containers are measured the same way. A FLAC whose audio carries a
steep codec corner is flagged too, because the wrapper does not make the
content lossless; the review then reads "the container is lossless, but the
audio stops at ...". A genuinely lossless file, including 96 kHz sources, is
never flagged: only a codec-style corner counts, not a dark mix.

It needs ffmpeg and ffprobe on the host; without either, files simply skip the
check. It stays honest about what a spectrum can and cannot prove: a high end
that fades out gently is music that was mixed that way, not a mislabelled rip,
and Opus files stay near full band even at low bitrates, so a dark mix or an
Opus file is never called inflated and never gets a quality hint. Only a steep
codec corner, well below what the declared bitrate needs, counts.

<img src="/screens/playlist-details.webp" alt="Playlist details page with the setup panel and its archive destination checklist" width="800">

<img src="/screens/playlist-tracks.webp" alt="Track rows with a length review and a spectrum review, each offering Keep and Retry" width="800">

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

Re-adding a playlist while the app is running adopts its files right away, from
the playlist folder: a file with the matching `MELODY_ID`, an old `mb-` id, or
no id at all is matched by its tags and artist, title and length, then given
the deterministic id. The **File links** action on the Advanced page runs the
same repair over the whole library and then reports numbered duplicates.

## Playlist folders

Every playlist downloads into its own folder named after it, under the music
path: `/music/My Summer Hits/...`, liked songs included (`/music/Liked songs
(Spotify)/...`). The default archive copy mirrors that layout under the archive
path, and every extra profile mirrors it under its own root, so two libraries
never mix one playlist's files with another's. That separation is
what makes the duplicate handling below possible: a linked file lands in the
playlist's own folder, never somewhere shared.

Files that were downloaded before their playlist had a folder still sit loose
under the music path or archive path. On startup MelodyBridge moves those into
the playlist's folder and updates its bookkeeping, so the old layout heals by
itself. A file that several playlists share (from the old shared-folder layout)
is moved once and every playlist keeps pointing at it. Changing the music path
or archive path under **Settings, Default paths**
does the same on save: the tracked files, the playlist folders and the scan
locations follow the new root. Files outside those paths, and destinations that
already hold a file, are left alone; empty folders the move leaves behind are
removed, a folder with anything else in it stays.

Type a folder on the playlist page to override the location. A folder you type
is used as it is; leave the field empty and the default music path plus the
playlist folder applies. A grey `{playlist}` suffix next to the field shows
when the playlist folder is what gets appended, and **Sort downloads into a
folder per playlist** under Settings turns the per-playlist folders off for a
flat library.

## Duplicate tracks <Badge type="tip" text="Optional" />

The same track often sits in more than one playlist. Downloading identical bytes
again would waste disk space, so before a download MelodyBridge checks whether
the same `MELODY_ID` is already sitting in another playlist folder:

- **Same quality.** The existing file gets a hard link into this playlist's
  folder: a second name for the same bytes, costing zero extra space. Both
  folders stay normal folders with normal files in them. A lossless container
  matches on the container alone, so a FLAC without a measured bitrate still
  links.
- **Different quality.** Nothing is guessed. The track pauses as **needs
  review** with two buttons: **Download new** fetches a fresh file in this
  playlist's quality, **Hardlink existing** asks for confirmation first and
  then accepts the other file as-is. The dialog names the quality difference
  and explains that both playlists will share the same bytes.
- **Different drive.** A hard link only works within one filesystem. When the
  folders sit on different drives the track simply downloads the normal way; a
  copy pretending to be a link is never used.

Removing a track from one playlist removes only that playlist's name for it.
The bytes stay as long as any other playlist still links them. The whole feature
can be turned off with **Hardlink duplicate tracks across playlists** on the
Advanced page. A linked track reuses the original's archive copies too: when the
other playlist already has one, its bytes are linked into this playlist's archive
folder, and the named profiles are filled from the same primary, so both
playlists get their copies without a second download.

Linking does not wait for a download. When you add or refresh a playlist, tracks
the library already holds are linked right away, so a new playlist owns its own
file names from the first moment. The same pass runs when a library scan finds
files for a playlist that had none. A quality difference never pauses anything on
its own: it only asks for your decision when a download would otherwise put a
different version next to the existing one.

## Archive copies <Badge type="tip" text="Optional" />

Archive copies give every download a second home. The use case is simple: keep a
FLAC archive for the long term, and let your media server stream smaller Opus or
AAC files. Or keep two archives, one Opus 128 and one MP3 160, so every player
has a compatible copy.

The first copy is the **default archive** you set under **Settings, Default
paths**. Below it, **Archive profiles** add more destinations. Each profile has
a name, folder, container and bitrate. The playlist page selects the default
archive and any named profiles as one checklist, so a playlist can write no
copies, one copy, or several. A playlist from an older version keeps its
previous folder and format as an **Old playlist archive** row until you save a
new selection, so an upgrade does not silently move its copies. The main file
is still fetched once; each archive is a local ffmpeg conversion, so three
copies do not mean three downloads.

Archive folders are never mistaken for a track's main file: a library scan skips
every configured archive root, and the dedup linker ignores files that live in
one. Downloading a track again replaces the files the row points at. A format
change can leave the previous copy under its old name, and removing a playlist
or track with the keep-files option leaves the copies on disk; neither is
deleted automatically. **Build missing** fills the extra-profile copies that a
newly added profile did not have yet, in the background, for the playlists
that select that profile. It skips every copy that is already on disk, so a
repeat pass is quick, and shows its progress on Settings and Needs attention.
The default copy is rebuilt per track from its row action.

| Format | Result |
|---|---|
| App default | Whatever the default archive format under Settings says |
| Auto | A plain copy of the downloaded file |
| Opus | 128 kbps by default; add a bitrate such as `opus:192` |
| FLAC | Lossless, level 5 compression |
| MP3 | 256 kbps by default; `mp3:160` and `mp3:320` are also offered |
| AAC | 192 kbps by default, lands as `.m4a` |
| Custom | Any container ffmpeg can write, with an optional `container:codec:bitrate` set such as `m4a:alac` or `mp3:libmp3lame:160`; without a codec ffmpeg uses the container default |

The track is fetched from the source once. The archive copy is produced locally
from that file with ffmpeg, so a second library never costs a second internet
download. If ffmpeg is missing, the download still succeeds and the track gets
an **archive copy failed** warning instead. Two songs that would land on the same
name in a flat archive folder both get their copy: the second one is written as
`Song (2).opus`. The same recording found on two platforms (Spotify and Apple
Music, say) is recognised by its ISRC, its tags or its bytes, and shares one
copy instead of being written twice. The file column on the playlist
tracks (enable it on the Advanced page) shows an `archive: <filename>` chip on
tracks that have an archive copy, and a small check next to the status column
tells you at a glance whether the copy exists, is missing from disk, or was
never made.

## Needs attention

The app classifies every failed or flagged track into one stable kind, so the
global [Needs attention page](/reference#the-needs-attention-page) can group
them: no source, quality filter, wrong format, wrong length, invalid audio,
corrupt file, login, timeout, cancelled, missing metadata, other, hardlink
decision, length review, spectrum review, archive copy, missing file or
unverified file. Classification reads the stored status, error and warning
texts; a normal download and an informational warning such as a quality hint are
not attention.

The page is a queue, not a log. It opens in **Automatic** mode: the repair card
summarizes the per-group actions, **Fix all** applies them in one run, and a
progress bar with **Stop** keeps the page responsive. The filters show one
category at a time instead of one long scroll. The repair runs in the
background, so navigating away does not stop it; the Downloads page shows the
job with its queue and ETA. The mapping is deliberately
different per group: retry for failed downloads and missing files, keep for
length reviews, retry for quality reviews, hardlink for duplicates, rebuild for
archive problems, re-run the audio check for unverified files and a re-sync for
playlists. **Review individually** exposes the per-row and per-group
actions. Retrying a group runs one track at a time and finishes with a summary
instead of stopping at the first failure. Hardlink actions only look for a file
that is already in the library, so they cost no network download. Archive actions
rebuild the default copy and every enabled profile. A fixed plugin login or a
resolved review disappears from the queue on the next refresh.
