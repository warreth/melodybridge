---
layout: home

hero:
  name: MelodyBridge
  text: Your playlists, your files.
  image:
    src: /screens/home.webp
    alt: MelodyBridge dashboard with playlist cards and download status
  tagline: Save a Spotify or YouTube playlist, download the tracks through a plugin waterfall, and publish them as M3U, Jellyfin, Plex or Navidrome playlists — on your own server.
  actions:
    - theme: brand
      text: Quick start
      link: /quickstart
    - theme: alt
      text: GitHub
      link: https://github.com/warreth/melodybridge

features:
  - icon: 🎵
    title: Multi-source downloads
    details: A waterfall of plugins — Lucida, SoundCloud, Internet Archive, yt-dlp and more — tries each track until one passes your quality bar.
  - icon: 🐳
    title: Self-hosted, one container
    details: Docker Compose starts the app and its Cloudflare solver. Your music and database stay in one ./data folder you own.
  - icon: 🔍
    title: Real quality checks
    details: "Files are measured, not trusted: bitrate probing, duration verification and a spectral check that catches fake 320 kbps rips."
  - icon: 📤
    title: Publish anywhere
    details: Write M3U files with full metadata or push playlists straight into Jellyfin, Plex or Navidrome, on the schedule you pick.
---
