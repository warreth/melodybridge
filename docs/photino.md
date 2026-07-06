# Photino Desktop Build

Photino is provided as an **optional** desktop wrapper around the Blazor UI. The recommended deployment is the Docker server — it is fully supported and continuously tested.

> **Note:** Photino builds are not published in CI by default.

---

## Prerequisites

- .NET SDK 8.0+
- Platform-specific runtime (see Photino documentation)

---

## Build Steps

### Linux

```bash
dotnet publish MelodyBridge.Desktop/MelodyBridge.Desktop.csproj \
  -c Release -r linux-x64 --self-contained=false
```

### Windows

```bash
dotnet publish MelodyBridge.Desktop/MelodyBridge.Desktop.csproj \
  -c Release -r win-x64 --self-contained=false
```

### macOS

```bash
dotnet publish MelodyBridge.Desktop/MelodyBridge.Desktop.csproj \
  -c Release -r osx-x64 --self-contained=false
```

---

## Output

The published binaries are placed in:

```
MelodyBridge.Desktop/bin/Release/net8.0/<rid>/publish/
```

---

## Distribution Notes

- Package per-platform installers using your preferred tooling (Inno Setup, DMG, etc.)
- Sign binaries with a code-signing certificate before distribution
- The Photino window wraps the same Blazor UI as the web server — no feature differences

---

## Troubleshooting

| Issue | Likely Cause | Fix |
|---|---|---|
| Blank window | Missing ASP.NET runtime | Install `aspnetcore-runtime-8.0` |
| API calls fail | Backend not running | Ensure the server is accessible |
| Missing icons | Photino assets path | Verify `wwwroot/` is published |
