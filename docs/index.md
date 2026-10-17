---
layout: home

hero:
  name: MelodyBridge
  text: Your playlists, your files.
  image:
    src: /screens/home.webp
    alt: MelodyBridge dashboard with playlist cards and download status
  tagline: Save a Spotify or YouTube playlist, download the tracks through a plugin waterfall, and publish them as M3U, Jellyfin, Plex or Navidrome playlists, on your own server.
  actions:
    - theme: brand
      text: Quick start
      link: /quickstart
    - theme: alt
      text: GitHub
      link: https://github.com/warreth/melodybridge

features:
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5h16l-6 7v5l-4 2v-7L4 5z"/></svg>'
    title: Multi-source downloads
    details: "Qobuz, Tidal, Deezer, Soulseek, Monochrome, Lucida, SoundCloud, Internet Archive and yt-dlp. Each track goes down the waterfall and stops at the first file that passes your quality bar."
    link: /concepts#the-waterfall
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3z"/><path d="M12 12l8-4.5M12 12v9M12 12L4 7.5"/></svg>'
    title: Self-hosted with Docker
    details: "docker compose up -d starts the app and its Cloudflare solver. Music, playlists and the database land in ./data, folders on your disk you can hand to Jellyfin, Plex or Navidrome."
    link: /docker
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h2M7 8v8M11 5v14M15 9v6M19 12h2"/></svg>'
    title: Real quality checks
    details: "Every download is probed for real bitrate and duration, and checked byte by byte. A spectral analysis catches files that claim 320 kbps but hold far less."
    link: /concepts#quality-checks
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 16V4M7 9l5-5 5 5M5 20h14"/></svg>'
    title: Publishing integrations
    details: "Write M3U files or push playlists into Jellyfin, Plex and Navidrome. Run a job by hand, or let it run nightly."
    link: /publishing
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h7l9 9-7 7-9-9V4z"/><circle cx="8.5" cy="8.5" r="1.5"/></svg>'
    title: Resilient library
    details: "Every file carries a source-derived MELODY_ID tag. Delete the database, re-add your playlists, and the music you already have links back. Nothing downloads twice."
    link: /concepts#surviving-a-database-wipe
---
