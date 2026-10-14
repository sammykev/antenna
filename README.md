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
| EPG guide (XMLTV, .xml or .xml.gz; auto-detected from url-tvg / Xtream) | ✅ |
| "Now playing" on Live TV tiles and in search | ✅ |
| Guide channel matching by tvg-id, then by name ("UK: BBC One HD" ↔ "BBC One") | ✅ |
| Catch-up: Xtream timeshift (server time-zone aware) | ✅ |
| Catch-up: M3U `catchup` modes default / append / shift / flussonic / xc | ✅ |
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

## Run it on your PC

### Option A: download a ready-made build (no tools needed)

1. Open the repository's **Actions** tab on GitHub and select the latest green **Antenna** run.
2. Under **Artifacts**, download **Antenna-win-x64** (you must be signed in to GitHub) and unzip it.
3. Double-click `Antenna.App.exe`. It's self-contained: no .NET or Windows App SDK install needed.
   If SmartScreen warns about an unknown publisher, choose **More info → Run anyway** (the build is unsigned).

### Option B: build from source

1. Install the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` (or download it from dot.net).
2. Get the code and switch to the branch:
   ```powershell
   git clone https://github.com/sammykev/miva-notes.git
   cd miva-notes
   git checkout sammykev/zen-ptolemy-3t0ch1
   cd antenna
   ```
3. Run it:
   ```powershell
   dotnet run --project src/Antenna.App -p:Platform=x64
   ```
   On an ARM PC (e.g. Snapdragon), use `-p:Platform=ARM64`.

### First steps in the app

1. **Playlists → Add playlist**: paste your provider's M3U URL, or choose **Xtream Codes** and enter
   the server, username and password.
2. Wait for "loaded N items and M guide entries".
3. Browse **Live TV / Movies / Series**, search from the box at the top of the pane, or open the **Guide**.
   In the guide, click what's on now to watch live, or a past programme marked **▶ Catch-up** to replay it.
   Catch-up only appears for channels your provider archives.

## Build and run (Windows), developer notes

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
