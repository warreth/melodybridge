# Account login: OAuth today, cookie-based tomorrow

## What we have today

MelodyBridge connects Spotify and YouTube through the **official OAuth flows**:

- **Spotify**: OAuth PKCE, read-only scopes (private + collaborative playlists,
  liked songs). Requires a Client ID from developer.spotify.com.
- **YouTube**: OAuth authorization-code flow, read-only. Requires a Google
  Cloud OAuth client (id + secret).

Both flows are the **stable, ToS-compliant option** and stay the default.

## What passwordless "just log in" apps (Meld, Spotube forks) actually do

[Meld](https://github.com/FrancescoGrazioso/Meld) advertises "log in with your
Spotify account, no Client ID". Under the hood (`spotify/SpotifyAuth.kt`) it:

1. Opens `accounts.spotify.com` in an **embedded WebView**.
2. After login it **scrapes the `sp_dc` / `sp_key` session cookies** from the
   WebView cookie jar.
3. Exchanges those cookies for a web-player token at
   `https://open.spotify.com/api/token`, guarded by a **TOTP** generated from
   a shared secret fetched from a **community-maintained GitHub gist**
   (`api.github.com/gists/22ed9c6…`) that Spotify rotates periodically.
4. Uses that internal web-player token against the **private, undocumented
   `spclient` API** (the same endpoints the web player itself uses).

### Verdict on stability

| Aspect | Assessment |
| --- | --- |
| Works without a Client ID | Yes |
| Official / ToS-compliant | **No** — internal API, cookies, anti-bot TOTP |
| Stable | **Fragile**: the TOTP secret rotates, the gist must be updated by the community, and Spotify can break the flow (or ban) at any time |
| App distribution | Fine for sideloaded Android apps; a **bad default for a self-hosted server** whose headless container cannot run a WebView |

## Recommendation for MelodyBridge

- **Keep OAuth as the default and only built-in login.** It is the only
  durable, documented path, and both providers already work.
- **Remove the friction instead of the flow**: the web UI now pre-fills the
  canonical redirect URI so creating the developer app is a 2-minute copy
  paste; Client ID entry lives on the Plugins/Accounts page with step-by-step
  hints.
- Cookie-based (`sp_dc`) login stays out of scope: headless containers have no
  WebView to harvest cookies from, and the gist-TOTP dependency makes it too
  brittle for an unattended server. Revisit only if Spotify ever offers a
  first-party passwordless flow.
