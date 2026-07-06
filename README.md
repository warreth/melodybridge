# MelodyBridge

A self-hosted music retrieval and playlist-sync system focused on keeping downloads, metadata and playlists in sync across multiple storage locations and media servers.

**Your playlists, downloaded, tracked and delivered — reliably and modularly.**

---

## Features

| Feature | What it does |
|---|---|
| **Downloaders (pluggable)** | YouTube via yt-dlp by default; plugin architecture for Streamrip, Soulseek, Qobuz, Tidal, etc. |
| **MELODY_ID tagging** | Unique ID embedded in file metadata so the library can always identify tracks |
| **Library Detective** | Scans tag metadata (not filenames) to keep database paths current after moves/renames |
| **Playlist Composer** | Create M3U files or sync playlists to media servers (Jellyfin plugin included) |
| **Path Remapping** | Rewrite paths and extensions for containerized or converted libraries |
| **Docker-first** | Designed to run in Docker; Photino desktop build also available |

---

## Quick Start with Docker

```bash
docker compose up -d
```

Or build manually:

```bash
docker build -t melodybridge:latest -f Dockerfile .
docker run -d \
  -v ./data/music:/music \
  -v ./data/playlists:/app/playlists \
  -v ./data/keys:/root/.aspnet/DataProtection-Keys \
  -p 3333:80 \
  --name melodybridge melodybridge:latest
```

Open [http://localhost:3333](http://localhost:3333) and configure settings.

---

## Project Structure

```
MelodyBridge.sln
├── MelodyBridge.Core/              # Interfaces, contracts, enums, models
├── MelodyBridge.Infrastructure/     # Implementations: downloaders, scanners, taggers, media servers
├── MelodyBridge.Application/       # Orchestration: SyncEngine, DownloadManager, DI extensions
├── MelodyBridge.Server/            # ASP.NET Blazor web UI + REST API controllers
├── MelodyBridge.Desktop/           # Optional Photino desktop wrapper
├── MelodyBridge.UI.Components/     # Shared Blazor components
└── MelodyBridge.Tests/             # NUnit test suite (231+ tests)
    ├── Core/                       # Model, enum, mapping tests
    ├── Infrastructure/             # Scanner, tagger, M3U, Python runner, DB context tests
    ├── Services/                   # SyncEngine, DownloadManager, registry tests
    ├── Providers/                  # Music provider (Lucida, SquidWtf, etc.) tests
    └── Server/                     # ASP.NET controller tests
```

---

## Documentation

| Document | Description |
|---|---|
| [docs/index.md](docs/index.md) | Full documentation landing page |
| [docs/docker.md](docs/docker.md) | Docker deployment guide with compose examples |
| [docs/developer.md](docs/developer.md) | Plugin interfaces, architecture, and testing guide |
| [docs/photino.md](docs/photino.md) | Photino desktop build instructions |

---

## Testing

The project has **231 unit tests** covering all layers. Run them with:

```bash
dotnet test MelodyBridge.Tests/MelodyBridge.Tests.csproj
```

Tests use NUnit 4 + Moq + EF Core InMemory. See [docs/developer.md](docs/developer.md#testing) for the testing guide.

---

## Contributing

Contributions are welcome. See [docs/developer.md](docs/developer.md) for plugin guidelines, architecture notes, and the testing guide.

---

## License

This project is currently unlicensed in the repository. Add a license file before distributing binaries.
