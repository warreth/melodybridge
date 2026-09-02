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

1. Create an app at developer.spotify.com. No secret needed.
2. Under Redirect URIs, add the exact URL the Settings page shows
   (`http://127.0.0.1:PORT/auth/callback?provider=spotify` when you
   browse via the loopback IP, or your deployment's equivalent).
3. In Settings, paste the Client ID and press **Connect Spotify**.

Spotify PKCE keeps the refresh token; MelodyBridge stores it in its
database and refreshes access tokens automatically. Reconnecting does
not break the old login: Spotify keeps it valid.

## YouTube

1. In Google Cloud Console, enable the YouTube Data API v3 and create
   OAuth credentials of type Web application.
2. Add the YouTube redirect URI the Settings page shows
   (`http://127.0.0.1:PORT/auth/callback?provider=youtube` or your
   deployment's equivalent) as an authorized redirect URI. For testing
   outside Google's verification you must add yourself as a test user
   on the OAuth consent screen.
3. In Settings, paste the client id and secret and press
   **Connect YouTube**.

Liked songs arrive through the channel's `likes` playlist (`LL`);
private playlists through the normal Data API. Rate limits are generous:
quota counts per playlist page.

## Jellyfin favorites

Tracks imported from your liked songs are flagged, and the Jellyfin sync
marks them as favorites for the configured user (Settings, Jellyfin
panel). With no user configured, the first non-system user is used.

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
