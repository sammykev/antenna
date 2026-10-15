# Antenna roadmap

What to build next, in priority order. Tick items off (`- [x]`) as they ship and note the release.
Working notes for whoever picks this up are at the bottom; read them before changing anything.

Current release: **v0.1.7** (live pause/rewind in 0.1.6; picture and sound menu and multi-view presets in 0.1.7). Waiting on real-world feedback on pause/rewind before the next items.

---

## 1. Reliability (do first)

- [x] **Launch-test every build before it can be released.** *(v0.1.4: `--smoke-test`, run by build and release workflows)*
  v0.1.0 shipped unable to open (its `resources.pri` was missing) because nothing ever started the
  published build. In `release.yml` (and `build.yml`), after publishing: start `publish/Antenna.App.exe`
  on the runner, wait ~20 s, confirm the process is still alive and that
  `%LOCALAPPDATA%\AntennaData\logs\antenna.log` contains `INFO Started` and no `ERROR` lines, then
  close it. Fail the job otherwise. Consider a hidden `--smoke-test` argument that starts up, logs,
  and exits 0 by itself so the check doesn't need to kill the process.

- [x] **Multi-view: stop two streams from the same account knocking each other off.** *(v0.1.4: tile stops after 2 drops in 3 min, offers "Watch on <other playlist>" via `LibraryStore.FindSameChannel`)*
  From a real log: the provider reports `3 months allows 1 connection(s)`, and tiles 1 and 2 (both on
  that account) take turns failing every 25–50 s (`stream failed or ended (attempt 1)` → reopen → the
  other one drops). Plan:
  - When a tile fails while another tile on the same account is playing, show it on the tile:
    "Your provider allows 1 stream at a time".
  - Offer the same channel from another playlist (match by EPG id, then normalised name) and switch
    to it in one click; do it automatically if that's unambiguous.
  - Stop the endless reconnect loop after the limit is detected (keep the tile paused with a Retry button).
  - Code: `src/Antenna.App/Views/MultiViewPage.xaml.cs` (`OnFailed`, `ShowLimitHint`,
    `CheckLimitsAsync`, `StreamLimits`). The provider's reported limit is advisory only; earlier we
    blocked tiles on it and the user said both used to play, so never block up front.

- [x] **Crash reporting the user can act on.** *(v0.1.4: `Services/CrashReport.cs`; message box for fatal errors, "Copy details" on the error notice)*
  On an unhandled or start-up failure, show a small window ("Antenna hit a problem") with the error
  summary, **Copy details** (last ~200 log lines) and **Open log folder**, instead of only writing the log.
  Hooks already exist in `App.xaml.cs` (`UnhandledException`, `Step(...)`). Fatal start-up steps should
  still show it before exiting.

- [x] **"What's new" after an update.** *(v0.1.4: notes from commit subjects via `vpk pack --releaseNotes`; `Services/WhatsNew.cs` shows them once after updating, first visible when updating from 0.1.4)*
  - Release notes: generate from commit subjects since the previous tag and pass them to
    `vpk upload github` (`--releaseNotes <file>`), so each GitHub release has notes.
  - In the app: after a restart-to-update (Velopack's `OnRestarted` / version change since last run,
    stored in settings), show a dialog with that release's notes (fetch from the GitHub release, or
    ship them in the package).

## 2. Watching

- [x] **Pause and rewind live TV (timeshift).** *(v0.1.6: `Core/Playback/TimeshiftBuffer` + `LiveCapture`, played through `TimeshiftInput`; ←/→ jump 30 s, badge shows time behind and Go live; HLS plays directly. Needs real-world testing.)* Keep the last 30–60 min of the current live stream in a
  disk ring buffer (libvlc `:sout` to a temp file, or LibVLC's timeshift module `--input-timeshift-path`),
  allowing pause, rewind, skip back to live. Show a "LIVE" / "−5:32" indicator on the seek bar.
- [x] **Programme reminders.** *(v0.1.5: Guide and Live events right-click → Remind me; `Services/Reminders.cs` shows a Windows notification + in-app notice 2 min before, with Watch now)* "Remind me" on guide entries and event search results → Windows toast at
  start time that opens the channel. Persist reminders in the library; check on a timer like `Recorder`.
- [x] **Player controls:** *(v0.1.7: Picture and sound menu + Z/S keys; `Views/PlayerPage.Picture.cs`)* aspect ratio / zoom / crop (4:3 channels), audio delay, deinterlace toggle,
  and a stats overlay (codec, resolution, bitrate, dropped frames from `Media.Statistics`).
- [x] **Multi-view presets.** *(v0.1.7: Presets menu: save/open/delete; `Core/Library/MultiViewPresets.cs`. Drag-to-swap tiles not done yet.)* Save the current tiles + layout as a named preset ("Saturday football") and
  open it in one click; drag to swap tiles.
- [ ] **One channel, several sources.** Group the same channel across playlists (EPG id, then normalised
  name), show it once with a source picker, and fall back to the next source automatically on failure.

## 3. Looks

- [ ] **Movie and series artwork.** Fill missing posters, backdrops, plot, rating, cast from TMDB (user
  supplies a free API key in Settings, like OpenSubtitles). Cache with `ImageCache`.
- [ ] **Better channel logos.** Fill missing logos from a public logo set matched by normalised name.
- [ ] **Home page.** Grow the start-up overview into a real home: Continue watching, Live now on favourites,
  Upcoming events today, Recently added movies/series. Keep the three count tiles at the top.
- [ ] **TV / 10-foot mode.** Large layout for remote or gamepad: bigger tiles, focus visuals, D-pad
  navigation everywhere (gamepad keys already handled in the player via `AddKey`).

## 4. Speed

- [ ] **Staged playlist loading.** For very large playlists (100k+ items) import Live first, then Movies and
  Series, and make each usable as soon as it lands.
- [ ] **Faster zapping.** Pre-open the next/previous channel in a hidden player so Page Up/Down is near instant
  (respect the provider's connection limit; skip when it's 1).

## 5. Distribution

- [ ] **Microsoft Store.** Store-signed MSIX removes the SmartScreen "unknown publisher" warning for everyone
  and gives Store updates (one-time developer account). Keep Velopack for direct downloads.
- [ ] **winget** manifest pointing at the GitHub release's Setup.exe.

## 6. Housekeeping

- [ ] Dependabot (or Renovate) for NuGet and GitHub Actions; actions/checkout and setup-dotnet run on
  deprecated Node 20, bump them.
- [ ] Unit tests for app-side logic that doesn't need UI (move it into `Antenna.Core` where possible).
- [ ] Turn on .NET analyzers and treat new warnings as errors.

---

## Working notes

**Repo and identity**
- Repo: `sammykev/antenna`, branch `main`. Commit as **Okechukwu Nkwocha
  <74432283+sammykev@users.noreply.github.com>** (author and committer). The owner wants to be the only
  contributor: no co-author trailers, session links, or tool names in commits, code, or docs.

**Building and testing**
- Core tests: `dotnet test tests/Antenna.Core.Tests` (runs anywhere).
- The WinUI app only fully builds on Windows (the XAML compiler is Windows-only). On Linux, type-check C# with
  `dotnet build src/Antenna.App -p:Platform=x64 -p:EnableDefaultPageItems=false -p:EnableDefaultApplicationDefinition=false -p:WindowsAppSDKSelfContained=false`
  and ignore errors about `InitializeComponent`, `XamlTypeInfo`, and "The name 'X' does not exist" **only where X
  is an `x:Name` in a .xaml file** (a missing `using` produces the same message for real types, e.g. `ToolTipService`):
  ```bash
  names=$(grep -rhoP 'x:Name="\K[^"]+' src/Antenna.App --include=*.xaml | sort -u | paste -sd'|')
  dotnet build src/Antenna.App -p:Platform=x64 -p:EnableDefaultPageItems=false -p:EnableDefaultApplicationDefinition=false \
    -p:WindowsAppSDKSelfContained=false 2>&1 | grep -E " (error|warning) " \
    | grep -v "InitializeComponent\|XamlTypeInfo\|PasswordBox.Password" | grep -vP "The name '($names)' does not exist" | sort -u
  ```
  CI (`build.yml`, windows-latest) is the real check.

**Releasing**
- `release.yml`: push a tag `vX.Y.Z`, or run the **Release** workflow with input `version` (use this when tag
  pushes are blocked). It tests, publishes self-contained win-x64, checks the app's `.pri` exists, signs with
  the `SIGNING_CERT_PFX` / `SIGNING_CERT_PASSWORD` secrets (self-signed cert from `scripts/New-SigningCert.ps1`),
  packs with Velopack (`vpk` 1.2.161, packId `Antenna`) and publishes a GitHub release with Setup.exe,
  portable zip, full + delta packages. Versions must increase.
- Installed copies update in-app (`Services/Updater.cs`, GitHub source, checks ~20 s after start then every 6 h).

**Pitfalls already hit (don't reintroduce)**
- `EnableMsixTooling` must stay `true`, or published builds lose `resources.pri` and can't open any window.
- `Program.cs` is the entry point (`DISABLE_XAML_GENERATED_MAIN`); `VelopackApp.Build().Run()` must run first.
- Data lives in `%LOCALAPPDATA%\AntennaData` (library.db, logs, images). The installer owns
  `%LOCALAPPDATA%\Antenna` and deletes it on uninstall. `AppDataFolder.MigrateFromLegacy()` moves old data once.
- libvlc on Windows: `Mute`/`Volume` act on the whole app's audio session. Silence a multi-view tile with
  `SetAudioTrack(-1)`, never `Mute`.
- Never call libvlc from its own event threads: marshal to the UI with `DispatcherQueue`, do libvlc work
  with `Worker.Run`. Detach `VideoView.MediaPlayer` before disposing.
- XAML can't parse `Gamepad*` VirtualKey names; add those accelerators in code.
- Don't expose core types with `required` members on public XAML-visible members (CS9035 in XamlTypeInfo).
- SQLite searches use `ReadAside` (own connection) so the UI never waits on the store lock.
- Signing failures: the workflow explains bad secrets (thumbprint pasted, `.cer` pasted, wrong password).
