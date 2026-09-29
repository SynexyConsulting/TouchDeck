# Touch Deck for Windows: design spec

Status: approved direction (stack + execution chosen by the user, 2026-09-27)

## Goal
A native, installable Windows application that replaces `tools/clip_helper.py` and `tools/flash.py` for the Touch Deck boards. It detects the devices, shows their firmware version, carries out everything the Python helper does, and adds a few features the helper couldn't have.

Done means: `build.ps1` produces an MSI; installing it gives a Start-menu app that finds a plugged-in board, shows its firmware version, and performs copy / paste / jiggler (PC-mode) actions. All tests pass.

## Stack
- **C# .NET 8 (LTS), WPF**, `net8.0-windows`, published **self-contained win-x64**, so no .NET install is needed on the PC.
- The tray icon uses WinForms' `NotifyIcon` (WPF has none).
- **Installer:** WiX v5 through the `WixToolset.Sdk` NuGet package. It is built by `dotnet build`, so there's nothing to install system-wide.
  - Per-user MSI (no admin).
  - Start-menu shortcut.
  - Upgrades replace older versions (fixed UpgradeCode).
- **Tests:** xUnit. Hardware smoke tests skip when no board is present.
- **Layout:**
  - `src/TouchDeck.Core`: all logic, testable, with no UI.
  - `src/TouchDeck.App`: the WPF shell.
  - `tests/TouchDeck.Tests`.
  - `installer/`.
  - `build.ps1`.
- Its own git repository in `windows-app/`, which the parent repo ignores.

## Devices (detection)
| Board | USB VID:PID | Serial control lines |
|---|---|---|
| RP2040 Touch Deck (1.69", future 1.28") | `CAFE:4011` | DTR **high** (TinyUSB only transmits with DTR) |
| ESP32-C3 Touch Deck | `303A:1001` | DTR **and** RTS **low** (they are reset / boot-mode) |

- COM ports are enumerated with their VID:PID from the PnP device IDs (`USB\VID_xxxx&PID_xxxx`).
- Hot-plug is handled by polling every 2 s: connect when a known board appears, clean up when it disappears.
- A port counts as a Touch Deck only once it answers `HELLO` with `PONG`.

## Firmware version (new firmware command)
- **PC → board:** `VER`.
- **Board → PC:** `VERSION <board> <semver> <build>`, e.g. `VERSION rp2040-169 1.5.0 2026-09-27`.
- Both firmwares define `FW_VERSION` and a board id.
- The app shows board, version and build.
- Firmware without `VER` shows as "unknown (pre-1.5)".

## Behaviour ported from the Python helper (must match)
- `HELLO` on connect, then `TIME hh:mm:ss`. The time is re-sent hourly and on connect.
- Heartbeat every 2 s (`PING`, or `DBG` when diagnostics are on).
- **COPY:**
  - Read the focused control's UI Automation selection, walking up to 4 parents for a TextPattern.
  - Fall back to the clipboard.
  - Transliterate to ASCII (same substitution table; non-ASCII becomes `?`), cap at 8192 bytes, and send `CLIP <n> <src>\n<bytes>`.
  - `src` is `select` or `clipbd`.
- **`K <mod> <usage>` / `M <btn> <dx> <dy>`:** perform them with `SendInput`.
  - Keys go in as **scancodes**, in the same order as `inject.py`: old key up, dropped modifiers up, new modifiers down, new key down.
  - Malformed input, bytes outside 0..FF, and unknown usages are ignored whole.
- **`LEDS`:** poll Caps Lock every 250 ms and send `LEDS 00|02` on change.
- **Release everything held** (keys, buttons) when the link drops or the app exits.
- **`LOG` lines** go to the app's log view.
- **Dry-run:** log injections instead of performing them.
- **Send text** to the board (the old `--send`).
- **Reboot to bootloader** (the old `--boot`).
- **Flash firmware** (the old `flash.py`, RP2040 only): `BOOT`, wait for the `RPI-RP2` UF2 drive, copy the `.uf2`, wait for the board to come back.

## New features
- **Tray-first app:** a tray icon shows the link state (connected / no device / error). Closing the window minimises to the tray, and the tray menu has Open / Dry-run / Quit.
- **Device panel:** board, port, firmware version and build, connected-since. When diagnostics are on it also shows live stats from `DBG`: uptime, fps, touch health, output mode, jiggler, stopwatch.
- **Clip history:** the last 10 clips sent to the board, each with a one-click resend.
- **Firmware update:** the installer bundles the RP2040 firmware (`watch.uf2`) and its version. When a connected RP2040 reports an older version, the app offers "Update firmware".
- **Start with Windows** toggle (HKCU Run key).
- **Settings** in `%APPDATA%\TouchDeck\settings.json`: dry-run, diagnostics, start with Windows.

## Out of scope
- ESP32-C3 firmware flashing (needs esptool).
- macOS.
- Code signing: there's no certificate. SmartScreen will warn on first run, which is documented.

## Testing
- **Unit:**
  - protocol parsing (lines, `VERSION`, `DBG` fields);
  - ASCII transliteration (the Python tests' cases);
  - injector ordering and robustness (all `inject.py` tests ported);
  - VID/PID parsing;
  - settings round-trip;
  - clip history;
  - the session logic against a fake transport and fake injector (copy, K/M, LEDS, release on disconnect).
- **Hardware smoke** (skipped without a board): find the device, `HELLO`→`PONG`, `VER`→`VERSION` parsed, `DBG` parsed.
- **Installer smoke:** build the MSI, install it per-user silently, check the files and shortcut exist, launch the app briefly, then uninstall cleanly.
