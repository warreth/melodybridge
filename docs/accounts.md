# Accounts and imports

Public links cover a lot, but your private playlists, collaborative playlists
and liked songs need a login. MelodyBridge connects to Spotify and YouTube
through their own OAuth flows and imports what only you can see.

Two design rules keep that safe:

- **Read access only.** Spotify asks for `playlist-read-private`,
  `playlist-read-collaborative` and `user-library-read`; YouTube asks for
  `youtube.readonly`. Nothing that could modify your account is ever requested.
- **Official libraries.** Spotify uses SpotifyAPI-NET's PKCE flow, YouTube uses
  Google's own auth libraries. No browser cookies are scraped.

Public fetching stays independent: when no account is connected, or an account
call fails for any reason, MelodyBridge falls back to the same page scraping and
yt-dlp it always uses.

## Import without Premium

The Spotify account connection needs a Premium account (Spotify requires it from
the owner of a developer app, and every self-hosted app is its own app). Free
accounts have two other routes, both from the Import button on the Playlists
page:

- **Exportify CSV (recommended).** Log in at
  [exportify.net](https://exportify.net), click *Export Liked Songs* (or any
  playlist), and upload the CSV. *Export All* gives you a zip, which you can
  upload as well. The file carries track names, artists, albums and Spotify
  IDs.
- **Spotify data export.** Request *Download your data* at
  [spotify.com/account/privacy](https://www.spotify.com/account/privacy) and
  upload the zip the moment Spotify emails it. The archive holds
  `YourLibrary.json` (liked songs) and `Playlist1.json` (all playlists);
  uploading the loose JSON files works too. This route is always manual,
  never automatic.

Uploads are read file by file, and a zip is unpacked in memory: one CSV in an
archive becomes one playlist. Each CSV becomes its own playlist, named after
the file, so different exports stay apart. Re-uploading a file refreshes that
playlist instead of duplicating it.

Each provider has its own redirect URI, differing only in a `?provider=` marker
that tells the callback which login to finish. Copy the exact URL from the
Settings page, query string included, because both platforms match it
character for character.

::: warning Spotify rejects localhost
Since April 2025 Spotify refuses `localhost` redirect URIs. When you browse the
app through localhost, MelodyBridge shows and sends
`http://127.0.0.1:PORT/...` instead. Register exactly that URL and both sides
match.
:::

## Spotify

1. Create an app at developer.spotify.com. No client secret is needed. When the
   dashboard asks which APIs you plan to use, pick **Web API**: the other
   options are for ads and in-app players and cannot read playlists.
2. In the app's settings, add the exact redirect URI that the MelodyBridge
   Settings page shows.
3. Copy the Client ID from the same page, paste it into Settings, and press
   **Connect Spotify**.

New Spotify apps run in development mode, which is fine for personal use: you
are the app owner. The owning account must have Premium, and only accounts you
allowlist in the app's user management can log in. Extended quota mode needs a
partner review that individuals cannot apply for, so a personal app simply
stays in development mode.

MelodyBridge stores the refresh token in its database and refreshes access
tokens on its own. Reconnecting does not break the old login.

## YouTube

1. In the Google Cloud Console, enable the YouTube Data API v3
   (APIs & Services, Library).
2. Create OAuth credentials of type **Web application** and add the redirect
   URI the MelodyBridge Settings page shows.
3. On the OAuth consent screen, add your own Google account as a **Test user**.
   An unverified app only works for its test users; the "unverified app"
   warning at login is expected here, choose *Continue to project*.
4. Paste the client ID and secret into Settings and press **Connect YouTube**.

Liked songs arrive through the channel's `likes` playlist. Private playlists
come through the normal Data API, and the quota comfortably covers a personal
setup.

## Liked songs become favorites

Tracks imported from your liked songs are flagged, and every sync job marks them
as favorites on the target: per user in Jellyfin, a top rating on the Plex
token-holder's account, and a star on the Subsonic side. See
[Publishing playlists](/publishing).

## Why there is no passwordless login

Some tools advertise "log in with Spotify, no Client ID needed". Under the hood
they open Spotify in an embedded web view, scrape the session cookies and trade
them for a web-player token through an undocumented endpoint that depends on a
TOTP secret some gist has to keep updating. It works until it does not, it is
not compliant with Spotify's terms, and it breaks every time the secret rotates.

MelodyBridge does not do that. OAuth with a client ID you own is a few minutes
of setup, and it keeps working.
