# CLAUDE.md

This is the Windows companion app for the Touch Deck boards: C# .NET 8 WPF with a WiX v5 per-user MSI. It lives in the `windows-app/` folder of the TouchDeck repo, next to the firmware. The protocol it speaks is documented at the top of `../src/usb_io.c`. It began as a port of the Python helper `tools/clip_helper.py`, now removed; git history has it.

## Commands

```powershell
dotnet test                      # all tests; hardware ones skip without a free board
.\build.ps1 [-SkipTests] [-Smoke] [-Version x.y.z]   # -> out\TouchDeck-<ver>.msi
.\tools\install-smoke.ps1 [-KeepInstalled]           # real install/run/uninstall check
src\TouchDeck.App\bin\Debug\net8.0-windows\TouchDeck.exe --smoke <dir>   # snapshot + state, then quit
```

- The app holds the board's serial port. Quit it (tray → Quit) before running the firmware repo's `flash.py` or its pytest board tests (they skip while the port is busy).
- `build.ps1` copies `../build/watch.uf2` and `FW_VERSION` from `../src/version.h` into `firmware/`. Rebuild the firmware first if you want the new version bundled.
- Scripts are Windows PowerShell 5.1, so keep them ASCII. Don't assign splat arrays from an `if` expression: a one-item array unwraps to a string and splats character by character.

## Architecture

- **Core has no UI.** `UseWPF` is on only for the UI Automation client assemblies, and it drops the implicit `System.IO` using, so add that using explicitly.
  - `Protocol/`: line parsing, `CLIP` framing, ASCII transliteration.
  - `Devices/`: WMI scan by VID/PID and the serial transport. DTR is high for the RP2040, whose TinyUSB only sends with DTR set. DTR and RTS stay low for the ESP32-C3, where they are its reset lines.
  - `Input/`: USB HID usage → scancode `Injector`, `SendInputSink`, and the dry-run `SwitchableSink`.
  - `Selection/`: UIA TextPattern with a 1.5 s time box, then the Win32 clipboard. When Touch Deck's own window is in front (the mirror was clicked), `FocusTracker` supplies the control last focused in another app: Windows clears a background app's focus window, so an out-of-context WinEvent hook (`EVENT_OBJECT_FOCUS`, handles only, its own message-loop thread) watches focus change. It never reads Touch Deck's own text.
  - `App/`: settings, RAM-only clip history, HKCU Run autostart, and the multi-board rules (`Boards.cs`: `LogEntry`, `BoardSelection`, `BoardLabels`).
  - `Firmware/`: manifest, UF2 validation (`Uf2.Inspect`: chip from family IDs, board from the `TDBOARD:` marker, payload reassembled by address), update flow, and new-board onboarding (`Onboarding.cs`).
- **`DeviceSession`** is single-threaded. `Run()` owns the transport on a worker thread; other threads only queue requests (`SendText`, `Swipe`, `PressButton`, `RequestBootloader`). `Run`'s `finally` always calls `Injector.ReleaseAll()`, so keep it that way. Lines read while waiting for a handshake reply are held and processed later, never dropped.
- **Device mirror (firmware 1.7.0+):** the main window's left column is the board itself.
  - `Core/Mirror`:
    - `UiState` is the firmware's `ui_state_t` field for field; a test pins its size (1248) against the renderer.
    - `MirrorState` applies `STATE` (all numeric fields are in `StateReport.Fields`; `page` marks 1.7.0), `TEXT` and `CLIPTEXT` lines.
    - `NativeUi` P/Invokes `tdui_rp2040.dll` / `tdui_esp32c3.dll` (the firmware's own page code) and returns RGB565, which WPF calls `Bgr565`.
    - `MirrorInput` maps a click to `TAP x y` and a sideways drag to `SWIPE L|R`.
  - The DLLs are built by `TouchDeck.Core.csproj` (target `BuildTdui`, incremental) with `../hostui/build.bat` into `src/TouchDeck.Core/obj/native`. They are copied next to the app, the tests and the publish folder, so the MSI harvests them. The build needs Visual Studio's C++ tools, found with vswhere; the CI runner has them.
  - Each `BoardController` keeps one `MirrorState` per session, applies lines on the UI thread and renders once per burst. `FullMirror` switches the window from the 1.6 jiggler card to `DeviceMirror`. If the renderer fails to load, the window keeps the jiggler card.
  - `DeviceSession` trims only CR/LF from lines, so `TEXT` values keep their spaces.
  - Smoke checks:
    - `--smoke DIR` also writes `mirror.png` (1:1, the selected board) and lists every tab in `smoke.txt` (`boards=`, `board_tabs=`).
    - `--smoke-steps` drives the board (pages, `ANIM`, a clip) and saves `mirror-*.png`; `../tools/mirror_check.py DIR` compares them with the board's framebuffer. `--smoke-board PORT` picks the board.
    - Debug builds only: `--smoke-demo-boards` adds two pretend tabs (a bootloader, a busy port) and saves `smoke-tabs.png`, `smoke-tab-busy.png`, `smoke-tab-new.png`, to check the tab strip with one real board.
  - Hardware tests share the xUnit collection `Board`, so they take the port one at a time.
- **Jiggler settings (firmware 1.8.0+):**
  - STATE carries `jmenu= jkey= jopen= jpause=`, giving `JigView.Config` → `JigConfig` (null on older firmware).
  - The Settings dialog's Jiggler section shows them and sends `DeviceSession.SetJigConfig` (`JIG CFG`). The controls follow what the board reports; there is no local state.
  - `Injector` maps F15 (usage 0x6A) to scancode 0x66 for the ESP32-C3's PC mode.
- **Board mirror (firmware 1.6.0+):**
  - The handshake ends with `WATCH 1`, and the board then streams `STATE` lines, parsed as `BoardLine` → `StateReport`.
  - `DeviceSession.MirrorSupported` is false if no `STATE` arrives within 1.5 s; the app then shows the "update the firmware" fallback text.
  - Commands: `SetJiggler`, `SetScale`, `ClearClip`.
  - `JigView` (Core) turns a `StateReport` into the texts the app shows.
  - `Jiggler/JigPaths.g.cs` is generated by `../tools/jigpaths.py`. Never edit it; regenerate with `python ../tools/jigpaths.py`.
  - `JiggleLane` (App) draws a letter like the device: a wide accent stroke, then the inner lane.
- **Several boards at once** (spec `../docs/superpowers/specs/2026-10-03-multi-board-design.md`):
  - `DeviceManager` polls every 2 s and keeps **one slot per port**, each with its own session and `LinkState` (Connected, `PortBusy` when another program holds the port, `NotResponding` with no PONG, Searching after its session ended). It raises `SlotChanged`/`SlotRemoved`/`SessionStarted` per port, on background threads, and never throws for a bad port. `Links`, `Sessions` and `SessionFor(port)` read it.
  - **`BoardController`** (App) is one tab: link, mirror, Remote actions, jiggler settings, diagnostics, the bundled and feed firmware offers, and new-board onboarding. **`AppController`** keeps the app-wide side (settings, app updates, clips, the log, the tray) plus `Boards` and `Selected`. `Selected` is never null: with no board it's a placeholder.
  - The window shows a pill per board once there are two (`ShowTabs`). The device column, the Remote card and the board parts of Settings bind to `Selected`; the code-behind re-subscribes to the selected board's events when it changes. A new board doesn't take the selection unless it's the only one or the last-used port (`settings.PreferredPort`).
  - The activity log is `LogEntry` items tagged with the board's port; **Only selected board** (shown with two or more boards) filters it, and Copy log copies what's shown.
  - The update check keeps the verified feed (`LastFeed`); each board's feed offer is worked out from it with no network call.
  - Pastes from two boards at once aren't serialized: an RP2040 types over its own USB HID.
- **`AppController`** marshals Core events to the dispatcher and ignores them after dispose. Shutdown order is controller, then tray: producers before consumers.
- **Unexpected errors** are appended to `%APPDATA%\TouchDeck\errors.log`.
  - The dispatcher backstop keeps the app running only once the main window exists. An exception during startup is not swallowed, because a windowless app would look alive and do nothing.
  - WPF event handlers must match the event's own args type, for example `MouseButtonEventArgs` for `MouseLeftButtonUp`. A mismatch fails in XAML at startup, not at compile time.
- **Firmware update:** it counts as done only when a session that wasn't there when the install started reports the target model (so another board of the same model can't be mistaken for it). One install at a time (`AppController.Installing`); the other boards keep working, the new-board scan pauses, and the board being installed keeps its tab while its port comes and goes.
- **Board models:** both RP boards are USB `CAFE:4011` (`BoardKind.Rp2040`), so anything model-specific uses the board `VER` reports.
  - `UiModel` (`Rp2040Rect`, `Esp32Round`, `Rp2350Round`, via `UiModels.For(kind, board)`) picks the renderer DLL, size, round shape and clipboard page.
  - `BoardKinds.DisplayName(kind, board)` names the round board.
  - `BoardModels` lists what the app can flash: `rp2040-169`, `rp2350-128`.
- **New boards:**
  - `NewBoards.Scan()` (every 2 s, paused during an install) finds Raspberry Pi USB IDs: stock SDK program `000A`/`0009`, bootloader `0003`/`000F`. Each is a tab: a stock board keyed by its COM port, a bootloader by `boot:<chip>` (a drive has no port, so two in their bootloader at once are one tab).
  - That tab's device card offers **Install Touch Deck** with the chip's model. A model picker appears only if a chip has more than one.
  - `FirmwareUpdater.InstallAsync(uf2, model, steps)` checks chip and model before touching the board. A pre-marker image counts only as `rp2040-169`.
  - It reboots a stock program at 1200 baud, uses that chip's drive, and requires the board to come back as that model.
  - Updates of either RP board go through it too.
  - `build.ps1` bundles `../build-rp2350/deck128.uf2` as `rp2350-128` when it's built.
- **Activity log:** scroll it via the dispatcher, never inside `LogLines.CollectionChanged`. The ListBox hasn't seen the new line yet, and WPF throws "ItemsControl is inconsistent". The crash backstop must never throw.
- **Installer:** installs to `LocalAppDataFolder\Programs\Touch Deck`. `ProgramFiles6432Folder` does not redirect for `perUser`. The payload is harvested with `<Files>`, and the main exe is a named `File` so custom actions can reference it.
  - `--quit`, run from the new package on `WIX_UPGRADE_DETECTED`, closes a running copy before InstallValidate.
  - `--cleanup` runs on a real uninstall and also removes the Run entry.
  - The app re-asserts the Run entry at start if the settings want it.
  - Per-user ARP entries live under `HKLM\...\Installer\UserData\<SID>`.
- **Visual language** matches the devices. The palette is from `../esp32c3/src/ui.cpp`, with Barlow and JetBrains Mono embedded (OFL, licenses shipped), pill buttons and an amber accent. Icons come from `tools/make_icons.py`.

## Updates and Settings

- **`Core/Updates`:**
  - `UpdateSource` (the official feed, or a loopback test feed) fixes which URLs are allowed, and which public key (`OfficialPublicKey`, pinned) must have signed the feed.
  - `FeedSignature`: `updates.json.sig` is base64 raw r||s ECDSA P-256 over the feed's exact bytes. An unsigned or wrongly signed feed is refused before it is parsed.
  - `UpdateClient` has timeouts: 30 s for the whole feed check, and 30 s of idle per download read. The feed and signature reads are size-capped.
  - The test switches `--update-feed`, `--update-key` and `--smoke-update` exist only in builds made with `build.ps1 -UpdateTestHooks`, never in a release.
  - `ClearDownloads` removes only the updater's own file names and refuses a junction in place of the folder.
  - `UpdateFeed.Parse` validates everything and rejects the whole feed on any bad entry: repo-only https URLs, 64-hex SHA-256, size caps of 200 MB for the app and 4 MB for firmware.
  - `UpdateClient` follows redirects by hand, only to GitHub https hosts, and hashes and size-checks while streaming. A mismatch leaves no file.
  - `UpdateService` downloads to `%LOCALAPPDATA%\TouchDeck\Updates` under fixed names.
  - `InstallerLaunch` starts a *fixed* encoded PowerShell script with paths passed only through environment variables. It runs `msiexec /i … /passive` and then relaunches the app.
  - Keep all of these properties; `UpdateTests.cs` pins them.
- **`SettingsWindow`:** an overlay window that dims the main window. It holds the versions, startup options (autostart with or without `--minimized`), updates and advanced options. `--smoke` also writes `settings.png`.
- **Autostart:** only the installed copy (`Autostart.IsInstalledCopy`) may re-point the Run entry at start-up. Dev and test builds must never hijack it.
- **Tests:** `tools/update-e2e.ps1` is the real in-app update test. `--smoke-update DIR` checks, reports to `update.txt`, and installs.

## Workflow

Work on feature branches and open a PR to `main` on GitHub (SynexyConsulting/TouchDeck). Test, see it pass, verify on hardware or with `--smoke`, then commit. Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
