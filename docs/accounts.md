# Accounts and imports

Public links cover a lot, but your private playlists, collaborative playlists
and liked songs need a login. MelodyBridge has three ways in: an official
Spotify login, an official YouTube login, and an unofficial Spotify web session
for free accounts.

Two design rules keep the official connections safe:

- **Read access only.** Spotify asks for `playlist-read-private`,
  `playlist-read-collaborative` and `user-library-read`; YouTube asks for
  `youtube.readonly`. Nothing that could modify your account is ever requested.
- **Official libraries.** Spotify uses SpotifyAPI-NET's PKCE flow, YouTube uses
  Google's own auth libraries. MelodyBridge never drives a browser, embeds a
  login page or reads cookies out of your browser.

Public fetching stays independent: when no account is connected, or an account
call fails for any reason, MelodyBridge falls back to the same page scraping and
yt-dlp it always uses.

## Import without Premium

The Spotify account connection below needs a Premium account (Spotify requires
it from the owner of a developer app, and every self-hosted app is its own app).
Free accounts have three routes, all from the Import button on the Playlists
page:

- **Unofficial web session (fully automatic).** Paste your own `sp_dc` cookie in
  Settings, Accounts, and liked songs plus playlists import like any other
  account. No developer app, no Premium, no manual file. Read the warnings
  before you turn it on.
- **Exportify CSV (no account access at all).** Log in at
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

## Unofficial Spotify web session

You do not need any of the above. The unofficial connection imports liked songs
and playlists on a free account, with no developer app and no Premium, because
it talks to Spotify the way the web player itself does. Open Settings, Accounts,
paste the `sp_dc` cookie from your own logged-in browser session, tick the
acknowledgement and press **Enable and test**. The test fetches your liked songs
and playlists right away, so a dead cookie fails on the spot instead of on your
first import.

::: warning Not for an account you care about
The cookie is a live credential: whoever holds it can act as your Spotify
account, and MelodyBridge stores it in plain text in its database next to your
media-server passwords. Spotify does not document this API and can change it at
any time, so imports can stop working until the app (or the secret list it
reads) catches up. Prefer the file imports above, or use a burner account.
:::

What you get, and what to know:

- Liked songs and every playlist the account can see, including private ones,
  with the same Import button and the same Update behaviour as the official
  connection.
- A Spotify link saved from the Playlists page tries the connected accounts
  first, the official app and then this session, which is what makes private
  playlists work. If no account can serve it, MelodyBridge falls back to public
  scraping.
- Nothing is written to your account, and no password is ever asked for or
  stored. Disconnecting deletes the cookie.
- The path depends on TOTP secrets that the community keeps in a public list.
  When Spotify rotates the format, the list has to move with it; the **TOTP
  secrets URL** field under the cookie input is there so you can point the app
  at a newer list without waiting for a release. The protocol knowledge comes
  from the GPL-3.0 Jellyfin Spotify import plugin by Viperinius and the
  projects it credits.
- The cookie stops working when you log out of Spotify in that browser. Paste a
  fresh one any time.

## Why the unofficial route is opt-in

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

## Why the unofficial route is opt-in

Some tools advertise "log in with Spotify, no Client ID needed". Under the hood
they open Spotify in an embedded web view, ask for your username and password,
scrape the session cookies and trade them for a web-player token through an
undocumented endpoint.

MelodyBridge does not do that. It never shows a Spotify login form, never asks
for a password and never reads cookies out of your browser. The web session
above is the manual, opt-in version: you copy one cookie yourself, you are told
what it costs you, and the official OAuth connection stays the supported path.
