# Contributing to MelodyBridge

Thanks for lending a hand. This page covers the practical bits; the
[developer guide](docs/developer.md) has the full architecture, plugin
interfaces and testing rules.

## Run the app from source

```bash
dotnet run --project MelodyBridge.Server
```

yt-dlp must be on PATH (`pip3 install --user --break-system-packages yt-dlp`).
Docker users can skip all of that: see the [Docker guide](docs/docker.md)
for the dev compose file that builds from your checkout.

## Tests

```bash
dotnet test MelodyBridge.sln   # fast suite
```

Live tests need yt-dlp and ffmpeg on PATH and hit the real network:

```bash
dotnet test MelodyBridge.sln --filter "Category=PlaylistStore|Category=Live"
```

The rules that matter: real SQLite files (never the InMemory provider for
persistence logic), assertions read back from disk or a fresh DbContext,
and downloaded files validated by their actual bytes.

## Docs

The documentation site lives in [docs/](docs/) and builds with VitePress:

```bash
npm install
npm run docs:dev    # preview on http://localhost:5173
npm run docs:check  # link + structure validation
npm run docs:build  # production build, also a dead-link check
```

CI builds the docs on every push to main that touches them.

## Pull requests

Small, focused PRs land fastest. Split unrelated changes across PRs, keep
the commit messages human (say what and why), and add a test for anything
that could regress. Open an issue first for big design changes.
