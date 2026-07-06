# Developer Guide

This document explains the architecture, plugin interfaces, DI setup, and testing practices for MelodyBridge.

---

## Architecture Overview

The solution follows a clean layered architecture:

```
MelodyBridge.Core               — Interfaces, contracts, enums, models (no dependencies)
       ↕
MelodyBridge.Infrastructure     — Implementations: downloaders, scanners, taggers, media servers
       ↕
MelodyBridge.Application        — Orchestration: SyncEngine, DownloadManager, DI extension methods
       ↕
MelodyBridge.Server             — ASP.NET Blazor web UI + REST API controllers
MelodyBridge.Desktop            — Optional Photino desktop wrapper
MelodyBridge.UI.Components      — Shared Blazor components
       ↕
MelodyBridge.Tests              — NUnit test suite targeting all layers
```

### Project Dependencies

| Project | References |
|---|---|
| Core | *(none)* |
| Infrastructure | Core |
| Application | Core, Infrastructure |
| Server | Core, Application, Infrastructure, UI.Components |
| Desktop | Server |
| Tests | Core, Infrastructure, Application, Server |

---

## Core Interfaces

### `IDownloaderPlugin`

```csharp
public interface IDownloaderPlugin
{
    Track DownloadTrack(SongID songID, TrackQuality quality);
}
```

Implement this to add a new download source. Place implementations in `MelodyBridge.Infrastructure/Downloaders/`.

### `IMediaServerSync`

```csharp
public interface IMediaServerSync
{
    string Name { get; }
    Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default);
}
```

Implement this to sync playlists to a media server (e.g., Jellyfin). Place implementations in `MelodyBridge.Infrastructure/MediaServers/`.

### `IMusicProvider`

```csharp
public interface IMusicProvider
{
    string ProviderName { get; }
    Task<Track?> SearchSingleAsync(string query, CancellationToken ct = default);
    Task<List<Track>> SearchAsync(string query, CancellationToken ct = default);
    Task<Track?> ResolveTrackAsync(string trackId, CancellationToken ct = default);
}
```

### Other Key Types

- **`Playlist`**, **`Track`**, **`TrackQuality`**, **`MediaType`** — Core models in `MelodyBridge.Core/Classes.cs`
- **`PlaylistOutputOptions`** — Output path, relative path toggle, path remap dictionary
- **`MediaServerSyncReport`** — Result report returned after a sync operation
- **`TrackEntity`**, **`PlaylistEntity`**, **`ProviderStateRow`** — EF Core entity classes for persistence

---

## Dependency Injection

The `MelodyBridge.Application` project provides extension methods for registering services:

### `AddMelodyBridge()`

Registers core services: `DownloadManager`, `SyncEngine`, library scanner, M3U generator, and all infrastructure services.

### `AddJellyfinSync()`

Registers the Jellyfin media server sync plugin with `HttpClient` via `AddHttpClient`.

### Usage in `Program.cs`

```csharp
builder.Services.AddMelodyBridge();
builder.Services.AddJellyfinSync(builder.Configuration);
```

---

## Testing

The test suite uses **NUnit 4** with **Moq** for mocking and **EF Core InMemory** for database tests.

### Running Tests

```bash
# Run all tests
dotnet test MelodyBridge.Tests/MelodyBridge.Tests.csproj

# Run with verbose output
dotnet test MelodyBridge.Tests/MelodyBridge.Tests.csproj -v n

# Filter by category or test name
dotnet test MelodyBridge.Tests/MelodyBridge.Tests.csproj --filter "FullyQualifiedName~SyncEngine"
```

### Test Organization

```
MelodyBridge.Tests/
├── Core/                  # Model, enum, mapping, provider qualities tests
├── Infrastructure/        # Scanner, tagger, M3U, DB context, Python runner tests
│   ├── DbContextTests.cs           # CRUD and edge cases for EF Core entities
│   ├── JellyfinSyncTests.cs        # Jellyfin server sync tests
│   ├── LibraryScannerTests.cs      # File scanning and metadata detection
│   ├── M3uGeneratorTests.cs        # M3U generation and path remapping
│   ├── TaglibHelperTests.cs        # Tag reading/writing tests
│   ├── TrackFileHelperTests.cs     # File download strategies
│   └── PythonRunnerTests.cs        # Python script execution tests
├── Services/              # SyncEngine, DownloadManager, registry tests
│   ├── SyncEngineTests.cs          # Core sync orchestration tests
│   ├── DownloadManagerTests.cs     # Download dispatch logic tests
│   └── MusicProviderRegistryTests.cs
├── Providers/             # Music provider API tests
│   ├── LucidaProviderTests.cs
│   ├── SquidWtfProviderTests.cs
│   ├── DoubleDoubleProviderTests.cs
│   ├── MonochromeProviderTests.cs
│   └── ProviderEdgeCaseTests.cs    # Null/empty/error handling tests
└── Server/                # ASP.NET controller tests
    └── SyncControllerTests.cs      # Sync API endpoint tests
```

### Testing Patterns

**InMemory Database** (for repository/EF Core tests):

```csharp
private MelodyBridgeDbContext CreateDbContext()
{
    var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
        .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
        .Options;
    var db = new MelodyBridgeDbContext(options);
    db.Database.EnsureCreated();
    return db;
}
```

**Mocking HttpClient** (for external API tests):

```csharp
var handler = new Mock<HttpMessageHandler>();
handler.Protected()
    .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
    .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
var client = new HttpClient(handler.Object);
```

**Mocking IMediaServerSync** (for SyncEngine tests):

```csharp
var mockServer = new Mock<IMediaServerSync>();
mockServer.Setup(s => s.Name).Returns("TestServer");
mockServer.Setup(s => s.SyncPlaylistAsync(It.IsAny<Playlist>(), It.IsAny<PlaylistOutputOptions>(), It.IsAny<CancellationToken>()))
    .Returns(Task.CompletedTask);
```

**Controller Tests** (using real InMemory DB):

```csharp
using var db = CreateDbContext();
var engine = new SyncEngine(db, new M3uGenerator(db, NullLogger<M3uGenerator>.Instance),
    Array.Empty<IMediaServerSync>(), NullLogger<SyncEngine>.Instance);
var controller = new SyncController(engine, NullLogger<SyncController>.Instance);
var result = await controller.Run(request, CancellationToken.None);
Assert.That(result, Is.InstanceOf<OkObjectResult>());
```

### Adding New Tests

1. Create a new `.cs` file in the appropriate folder under `MelodyBridge.Tests/`
2. Add `[TestFixture]` attribute to the class
3. Add `[Test]` or `[TestCase]` attributes to methods
4. Run with `dotnet test` to verify

---

## Adding a New Downloader Plugin

1. Create a class implementing `IDownloaderPlugin` in `MelodyBridge.Infrastructure/Downloaders/`.
2. Register it in the DI container via `ServiceCollectionExtensions`.
3. Add tests in `MelodyBridge.Tests/Infrastructure/`.

## Adding a New Media Server Plugin

1. Create a class implementing `IMediaServerSync` in `MelodyBridge.Infrastructure/MediaServers/`.
2. Register it via `ServiceCollectionExtensions`.
3. Add tests following the `JellyfinSyncTests` pattern.

---

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | ASP.NET environment |
| `ASPNETCORE_URLS` | `http://+:80` | Server binding |
| `Jellyfin__BaseUrl` | `http://host.docker.internal:8096` | Jellyfin server address |
| `Jellyfin__ApiKey` | *(empty)* | Jellyfin API key |
| `Logging__LogLevel__Default` | `Information` | Global log level |
