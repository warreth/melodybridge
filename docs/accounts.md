# Accounts and OAuth

MelodyBridge can log into your Spotify and YouTube accounts through the
platforms' own OAuth flows and import what only you can see: private
playlists, collaborative playlists and your liked songs.

::: tip Two design rules keep this safe
- **Read-only scopes only.** Spotify gets `playlist-read-private`,
  `playlist-read-collaborative` and `user-library-read`; YouTube gets
  `youtube.readonly`. Nothing that could ever modify your account is
  requested, so the account cannot be banned for behaviour it dislikes.
- **Official libraries.** Spotify uses SpotifyAPI-NET's PKCE flow,
  YouTube uses Google's own auth libraries. No logged-in browser cookies
  are ever scraped.
:::

The public fetcher stays independent. Account and public fetching live
in separate providers: when no account is connected, or the account call
fails for any reason, the public path (Spotify embed page scraping,
yt-dlp for YouTube) runs exactly as before.

## Importing liked songs without Premium

The API route above needs a Premium account (Spotify requires Premium
from the owner of a developer app). Free accounts have two other ways
into MelodyBridge, both from the Playlists page, Import button:

- **Exportify (recommended).** Log in at
  [exportify.net](https://exportify.net), click *Export Liked Songs*
  (or any playlist) and upload the downloaded CSV. It uses its own
  verified app, so free accounts work; the CSV carries track names,
  artists, albums and Spotify IDs.
- **Spotify data export (always manual, never automatic).** Request
  *Download your data* at
  [spotify.com/account/privacy](https://www.spotify.com/account/privacy),
  wait for Spotify's email (up to a few days), unzip the account-data
  package and upload `YourLibrary.json` (liked songs) or
  `Playlist1.json` (all playlists).

Re-uploading the same file refreshes the playlists instead of
duplicating them.

Each provider has its own redirect URI, differing only in a
`?provider=` marker that tells the callback which account to finish.
Always copy the exact URL from the Settings page — the platforms match
redirect URIs exactly, including the query string.

::: warning Spotify rejects localhost
Since April 2025 Spotify rejects `localhost` redirect URIs.
MelodyBridge automatically shows and uses `http://127.0.0.1:PORT/...`
when you browse via localhost; register the URL the Settings page
displays and both sides match.
:::

## Spotify

1. Create an app at developer.spotify.com. No secret needed. When the
   dashboard asks *which APIs you are planning to use*, pick
   **Web API** — the Ads API, Web Playback SDK, iOS and Android options
   cannot read playlists, they are for ads and in-app players.
2. In the app's Settings, add the exact redirect URI the Settings page
   shows (`http://127.0.0.1:PORT/auth/callback?provider=spotify` when you
   browse via the loopback IP, or your deployment's equivalent).
3. Copy the Client ID from the same Settings page, paste it in
   MelodyBridge and press **Connect Spotify**.

New apps run in **development mode**, which is fine for personal use:
you are the app owner. Two things to know — the owning Spotify account
must have Premium, and only you plus anyone you allowlist in the app's
Settings → Users Management can log in (others get a 403). Extended
quota mode needs a Spotify partner review that individuals cannot
apply for; a personal MelodyBridge stays in development mode forever,
and that is enough.

Spotify PKCE keeps the refresh token; MelodyBridge stores it in its
database and refreshes access tokens automatically. Reconnecting does
not break the old login: Spotify keeps it valid.

## YouTube

1. In Google Cloud Console, enable the YouTube Data API v3
   (APIs & Services → Library) and create OAuth credentials of type
   Web application (APIs & Services → Credentials).
2. Add the YouTube redirect URI the Settings page shows
   (`http://127.0.0.1:PORT/auth/callback?provider=youtube` or your
   deployment's equivalent) as an authorized redirect URI.
3. On the OAuth consent screen (APIs & Services → OAuth consent
   screen) add your own Google account as a **Test user** — an
   unverified app only works for its test users. The "unverified
   app" warning at login is expected for a self-hosted personal
   app; choose *Continue to project*.
4. In Settings, paste the client id and secret and press
   **Connect YouTube**.

Liked songs arrive through the channel's `likes` playlist (`LL`);
private playlists through the normal Data API. Rate limits are generous:
quota counts per playlist page.

## Media server favorites

Tracks imported from your liked songs are flagged, and the sync marks
them as favorites. Jellyfin does it per user: the configured user gets
the favorites (Settings, Jellyfin panel), and with no user configured
the first non-system user is used. Plex has one account in play — the
token-holder — so liked tracks get a top user rating there. Navidrome
stars them for the user whose username and password you configured.

## Why there is no passwordless login

Some apps advertise "just log in with your Spotify account, no Client
ID". Under the hood they open accounts.spotify.com in an embedded
WebView, scrape the `sp_dc` session cookies and exchange them for a
web-player token through an undocumented endpoint that depends on a
TOTP secret a community gist must keep updating, then talk to Spotify's
private `spclient` API with the result.

It works, but it is not compliant with Spotify's terms, it breaks every
time Spotify rotates the secret or changes the endpoint, and a
self-hosted server has no headless browser to harvest cookies from.
MelodyBridge keeps OAuth as the only built-in login. Revisit only if
Spotify ever offers a first-party passwordless flow.
