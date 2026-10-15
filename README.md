<p align="center"><img src="antenna-logo.png" width="128" alt="Antenna logo" /></p>

# Antenna

An IPTV player for Windows, built with WinUI 3 and LibVLC. It is an independent, clean-room
app with a feature set modelled on players like UHF on Apple platforms. It contains no code
or assets from any of them.

Antenna plays streams from playlists **you** supply (your IPTV provider's M3U URL or
Xtream Codes login). It doesn't include or find any content itself.

## Features

**Watching**
- Live TV, movies and series from M3U/M3U8 playlists (URL or file) and Xtream Codes accounts
- Instant channel switching in the player: Ch+/Ch− (Page Up/Down, gamepad bumpers), type a channel number, or pick from the channel list (L)
- Now/next overlay with channel logo and programme progress (I)
- **Pause and rewind live TV**: pause keeps buffering; ←/→ jump 30 s; the badge shows how far behind live you are, click it to go live (up to 30 minutes, on disk; HLS channels play directly)
- Continue watching: movies and episodes resume where you stopped
- Audio tracks, embedded subtitles, subtitle files, and OpenSubtitles.com search
- Picture and sound menu: aspect ratio and zoom-to-fill, audio delay for out-of-sync streams, deinterlacing, stream stats
- When a live channel keeps failing, Antenna switches to the same channel on another of your playlists (or pick one: Picture and sound → Watch on another playlist)
- Playback speed for VOD, mini player (always on top), full screen, auto-reconnect
- Cast to Chromecast devices on your network
- **Live events search**: type “man utd vs man city”, “NBA”, “Wimbledon” or tap a sport chip; finds live and upcoming matches across every guide (nicknames like *utd*, *spurs*, *psg* understood) and per-event channels; tick several and **watch them together**
- **Multi-view**: up to four matches playing at once; the selected tile is the main screen (slightly larger, the only one with sound) while the others keep playing silently; click a tile or press 1–4 to switch, Page Up/Down changes its channel; the channel picker lists every channel or every match for your search; save the current tiles as a **preset** ("Saturday football") and reopen them in one click
- **Picture-in-picture**: pop the player into a small always-on-top window (P) and keep browsing; double-click it to come back
- Streams that need a particular user agent or referrer (`#EXTVLCOPT`, `url|User-Agent=…`) now play

**Guide and catch-up**
- XMLTV guide (.xml / .xml.gz), matched to channels by id then by name
- List view per channel/day, or a timeline grid of every channel
- Catch-up for Xtream (time-zone aware) and M3U catchup modes (default, append, shift, flussonic, xc)

**Reminders**
- Right-click an upcoming programme in the guide or a match in Live events → **Remind me**: a Windows notification with **Watch now** appears 2 minutes before it starts (while Antenna is open)

**Recording**
- Record live TV now, or schedule from the guide (with padding); save catch-up to disk
- Download movies and episodes; Recordings page with progress, play and delete
- Scheduled recordings run while Antenna is open

**Library**
- Search across all playlists, with suggestions as you type; results can be narrowed to Live TV, Movies or Series, and matching **categories** show as chips you can open
- Search inside Live TV, Movies or Series (within the selected category, or all of them), and **Find a category** filters the category list
- Favourites, recently watched, continue watching ("My stuff")
- Pin categories to the top (and open them from My stuff → Pinned); hide categories; lock categories behind a parental PIN
- Edit, reorder, recolour and re-icon playlists
- Automatic background refresh of playlists and guides
- Backup and restore (playlists, favourites, category settings, preferences)

**Speed and looks**
- Lists load page by page off the UI thread; logos and posters are cached on disk
- Poster tiles and details pages (plot, cast, rating, backdrop) for movies and series; with a free TMDB key (Settings), missing posters and details are filled in from themoviedb.org
- Home page: how many live channels, movies and series you have (click one to go there), continue watching, favourite channels with what's on now, recently watched
- Light/dark/system theme, custom title bar, Mica
- **TV mode** (Settings): everything 1.5× bigger for a PC on the TV, arrow keys or a controller to move around, B to go back

**Security**
- Playlist passwords are encrypted with Windows DPAPI (only your Windows account can read them)
- Xtream credentials are kept out of stored stream URLs and guide links
- The parental PIN is stored only as a salted PBKDF2 hash

## Layout

```
antenna/
  src/Antenna.Core/        Cross-platform logic: playlists, Xtream, XMLTV guide, catch-up, SQLite library + search,
                           favourites/history/settings, backup, PIN hashing, OpenSubtitles, recording planner
  src/Antenna.App/         WinUI 3 app (Windows only)
  tests/Antenna.Core.Tests xUnit tests for the core library
```

## Install

1. Open **[Releases](https://github.com/sammykev/antenna/releases/latest)** and download **Antenna-win-Setup.exe**.
2. Run it. Antenna installs for your Windows account only (no administrator rights needed), adds Start menu
   and desktop shortcuts, and opens.
   Releases are signed with a self-signed certificate, so Windows SmartScreen may say "Windows protected your PC":
   choose **More info → Run anyway**, or trust the certificate first (see [Signing](#signing)).
3. Uninstall from **Settings → Apps → Installed apps → Antenna**. Your library stays in `%LOCALAPPDATA%\AntennaData`.

### Updates

Installed copies check GitHub Releases shortly after start and then every 6 hours, download the new
version in the background (only the changed files when possible), and show **Restart to update**.
If you don't restart, the update installs the next time you open Antenna.
**Settings → Updates** shows your version, has **Check for updates**, and can turn automatic downloads off.
Copies run from a folder or with `dotnet run` don't update themselves.

### Build from source

1. Install the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` (or download it from dot.net).
2. Get the code and run it:
   ```powershell
   git clone https://github.com/sammykev/antenna.git
   cd antenna
   dotnet run --project src/Antenna.App -p:Platform=x64
   ```
   On an ARM PC (e.g. Snapdragon), use `-p:Platform=ARM64`.
3. To update a source checkout later: `git pull`, then the same `dotnet run` command.

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
dotnet test tests/Antenna.Core.Tests
dotnet run --project src/Antenna.App -p:Platform=x64
```

Or open `Antenna.slnx` in Visual Studio, set `Antenna.App` as the startup project, pick `x64`,
and press F5. The app is unpackaged, so no certificate or MSIX install is needed.

Data lives in `%LOCALAPPDATA%\AntennaData` (`library.db`, `logs`, `images`). The installed app itself lives in `%LOCALAPPDATA%\Antenna`.

CI (`.github/workflows/build.yml`) tests the core, builds the app on `windows-latest`,
and uploads a self-contained `Antenna-win-x64` build as an artifact.

## Releasing a new version

1. Commit your changes and push them.
2. Tag the version and push the tag:
   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```
   (or run the **Release** workflow from the Actions tab and type the version).
3. `.github/workflows/release.yml` tests, publishes, signs, and packages the app with
   [Velopack](https://velopack.io), then creates a GitHub release with `Antenna-win-Setup.exe`, a portable zip
   and the update packages. Installed copies offer the update within a few hours, or straight away
   via **Check for updates**.

Versions must go up (`0.2.0` → `0.2.1` → `0.3.0`); installed copies ignore older ones.

## Signing

Releases are signed when two repository secrets exist (**Settings → Secrets and variables → Actions**);
without them, releases are built unsigned.

1. On your PC, in PowerShell in the repo folder:
   ```powershell
   .\scripts\New-SigningCert.ps1 -Trust
   ```
   It asks for a password and creates `antenna-signing.pfx` (keep it private and backed up),
   `antenna-signing.cer` (public) and `antenna-signing.pfx.txt`. `-Trust` makes this PC trust it (admin prompt).
   If PowerShell blocks the script, run `Set-ExecutionPolicy -Scope Process Bypass` first.
2. Add the secrets: `SIGNING_CERT_PFX` = the contents of `antenna-signing.pfx.txt`, and
   `SIGNING_CERT_PASSWORD` = the password.
3. On any other PC that should trust Antenna, double-click `antenna-signing.cer` → **Install Certificate** →
   **Local Machine** → put it in **Trusted Root Certification Authorities**, then repeat for **Trusted Publishers**.

A self-signed certificate proves releases come from you, but SmartScreen only stops warning on PCs that trust it.
To remove the warning for everyone, switch to a public certificate (e.g. Azure Trusted Signing): only the
"Prepare signing certificate" step of the release workflow changes.

## Keyboard shortcuts (player)

| Key | Action |
|---|---|
| Space / K | Play / pause |
| Page Up / Page Down (gamepad bumpers) | Next / previous channel |
| 0–9 | Type a channel number |
| L (gamepad Menu) | Channel list |
| I | Channel info (now / next) |
| P | Picture-in-picture |
| F, double-click | Full screen |
| Esc | Leave full screen / mini player |
| ← / → | Seek 10 s (live TV: jump 30 s back/forward in the pause buffer) |
| Z | Aspect ratio: Auto, 16:9, 4:3, 21:9, Zoom to fill |
| S | Stream stats (resolution, bitrate, dropped frames) |
| M | Mute |
| Gamepad B | Back |

## Licences

LibVLC and LibVLCSharp are LGPL-2.1-or-later. They're linked dynamically from their NuGet
packages, which keeps Antenna's own code under whatever licence you choose. Keep the LibVLC
licence notices if you distribute builds.
