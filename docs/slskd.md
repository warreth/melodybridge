# Soulseek and slskd

The Soulseek plugin pulls lossless FLAC and high-bitrate MP3 or AAC files from
Soulseek peers. It does not talk to the network itself: it drives
[slskd](https://github.com/slskd/slskd), the well-maintained Soulseek client
and daemon, over its REST API. That keeps the peer connection, the queueing and
the share handling in one place, and MelodyBridge only has to search and
collect the finished file.

## Setup

1. Run slskd where MelodyBridge can reach it, with a Soulseek account. Any
   free username works; the network registers new names on first login.
2. Give slskd folders for its downloads and incomplete transfers, and share
   at least some music. Peers regularly refuse downloads from empty shares.
3. Create a read-write API key in slskd's `slskd.yml`:

   ```yaml
   web:
     authentication:
       api_keys:
         - key: a-long-random-string
           role: readwrite
   ```

4. On the Plugins page, open the Soulseek card's **Settings** row and fill in
   the slskd URL (`http://127.0.0.1:5030` by default) and the API key.
   MelodyBridge asks slskd where it writes finished downloads and reads the
   file from there, so that folder must be visible to MelodyBridge too, at
   the same path. When the paths differ (a container mount), set the path
   MelodyBridge sees under **Advanced**.

The plugin reports itself unavailable until the URL and API key are set,
slskd is logged in to the network, and its downloads folder can be read, so a
half-finished setup never costs waterfall time.

## How a download runs

The plugin searches the network, skips locked files and containers it does not
declare, and ranks the rest on the usual title, artist and duration match.
Lossless files and peers with a free upload slot break ties. The transfer is
enqueued on the peer and polled until it finishes, then the file is moved out
of slskd's downloads folder into the playlist folder.

Soulseek transfers can sit in a peer's queue for a long time. The waterfall's
plugin time budget (four minutes by default, on the Advanced page) ends that
wait; raise it when you want MelodyBridge to wait longer for slow peers. Drag
the plugin above yt-dlp on the Plugins page when Soulseek should be tried
before YouTube.
