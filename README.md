# Antenna

An IPTV player for Windows, built with WinUI 3 and LibVLC. It is an independent, clean-room
app with a feature set modelled on players like UHF on Apple platforms. It contains no code
or assets from any of them.

Antenna plays streams from playlists **you** supply (your IPTV provider's M3U URL or
Xtream Codes login). It doesn't include or find any content itself.

## Status

| Feature | State |
|---|---|
| M3U / M3U8 playlists (URL or file) | ✅ |
| Xtream Codes accounts (live, movies, series + episodes) | ✅ |
| Search across all playlists (accent-insensitive, prefix) | ✅ |
| Colour- and icon-coded playlists | ✅ (colour picker; icon picker to do) |
| Playback: live and VOD, seeking, volume, full screen, keyboard shortcuts | ✅ |
| Multiple audio tracks and embedded subtitles | ✅ |
| Load subtitle files (.srt/.ass/.vtt) | ✅ |
| Auto-reconnect when the stream drops (VOD resumes in place) | ✅ |
| EPG (XMLTV) guide | ⏳ next |
| Catch-up (Xtream timeshift) | ⏳ URL builder done, UI next |
| Favourites and recently watched | ⏳ |
| OpenSubtitles.com search | ⏳ needs an API key |
| PIN parental controls by category | ⏳ |
| Offline downloads for VOD and catch-up | ⏳ |
| DVR recording | ⏳ |
| Secure credential storage (Windows Credential Locker) | ⏳ passwords are currently stored in the local database |

## Layout

```
antenna/
  src/Antenna.Core/        Cross-platform logic: M3U parser, Xtream client, SQLite library + FTS5 search
  src/Antenna.App/         WinUI 3 app (Windows only)
  tests/Antenna.Core.Tests xUnit tests for the core library
```

## Build and run (Windows)

Requirements: Windows 10 1809+ or Windows 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download),
and optionally Visual Studio 2022/2026 with the "WinUI application development" workload.

```powershell
cd antenna
dotnet test tests/Antenna.Core.Tests
dotnet run --project src/Antenna.App -p:Platform=x64
```

Or open `Antenna.slnx` in Visual Studio, set `Antenna.App` as the startup project, pick `x64`,
and press F5. The app is unpackaged, so no certificate or MSIX install is needed.

Data lives in `%LOCALAPPDATA%\Antenna\library.db`.

CI (`.github/workflows/antenna.yml`) tests the core, builds the app on `windows-latest`,
and uploads a self-contained `Antenna-win-x64` build as an artifact.

## Keyboard shortcuts (player)

| Key | Action |
|---|---|
| Space / K | Play / pause |
| F, double-click | Toggle full screen |
| Esc | Leave full screen |
| ← / → | Seek 10 s |
| M | Mute |

## Licences

LibVLC and LibVLCSharp are LGPL-2.1-or-later. They're linked dynamically from their NuGet
packages, which keeps Antenna's own code under whatever licence you choose. Keep the LibVLC
licence notices if you distribute builds.
