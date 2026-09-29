# Touch Deck for Windows: implementation plan

**Goal:** a working, installable Windows app (see spec) replacing `clip_helper.py` / `flash.py`.
**Spec:** `docs/specs/2026-09-27-touch-deck-windows-app-design.md`
**Execution:** inline (executing-plans). TDD per task; **commit per task** on branch `feature/app-v1` in this repo. Firmware changes (Task 5) go on a branch in the parent repo.
**Build/test:** `dotnet build TouchDeck.sln` · `dotnet test TouchDeck.sln` · `./build.ps1` (publish + MSI).

## Global constraints
- `net8.0-windows` for the App, `net8.0` + Windows APIs through P/Invoke for Core.
- The installed app must be self-contained win-x64.
- The protocol and injection behaviour must match `tools/clip_helper.py` and `tools/inject.py` in the parent repo, which are the reference.
- The Python tests are ported as C# tests with the same cases.
- VID:PID `CAFE:4011` (RP2040, DTR high) and `303A:1001` (ESP32-C3, DTR and RTS low).
- No blocking of the UI thread. Serial I/O and the session run on a background task, and state reaches the UI through events.
- Never leave a key or button held: release everything on disconnect and on exit.

## Review focus
1. **The board is unplugged during a paste:** held keys are released and the session cleans up without an exception.
2. **Two boards are plugged in at once:** the app picks one deterministically (RP2040 first) and shows which.
3. **A COM port is held by another program (e.g. the Python helper):** the app shows "port busy", keeps retrying, and doesn't crash.
4. **Firmware without `VER`:** shown as unknown, and firmware update is still offered for the RP2040.
5. **Very large or non-ASCII selections:** transliterated, capped at 8192 bytes, and the UI stays responsive.

---

### Task 1: Scaffold the solution
- Create `TouchDeck.sln`:
  - `src/TouchDeck.Core` (classlib net8.0-windows);
  - `src/TouchDeck.App` (WPF net8.0-windows, `UseWindowsForms` for `NotifyIcon`);
  - `tests/TouchDeck.Tests` (xUnit).
- `Directory.Build.props`: nullable, warnings as errors, a single `Version` for app and MSI.
- A placeholder test proves the harness runs.
- **Done when:** `dotnet build` and `dotnet test` are green. Commit.

### Task 2: Protocol and text (Core)
- `AsciiText.Transliterate(string) -> (string text, int lost)`: the same table as `clip_helper.ASCII_SUBS`, plus NFKD folding.
- `ClipMessage.Encode(text, src) -> byte[]`: the `CLIP n src\n` header plus up to 8192 ASCII bytes.
- `BoardLine.Parse(string) -> BoardMessage`: `Copy`, `Log(text)`, `Key(mod, usage)`, `Mouse(btn, dx, dy)`, `Pong`, `Version(board, semver, build)`, `Dbg(fields)`, `Unknown`.
  - Hex bytes outside 0..FF, or a missing field, make the line `Unknown` (ignored).
- **Tests:** the Python transliteration cases, `CLIP` framing and cap, every message kind, and the malformed lines from `test_helper.py`.

### Task 3: Input injection (Core)
- `IInputSink.Send(IReadOnlyList<InputEvent>)`, with `SendInputSink` (P/Invoke `SendInput`, scancodes) and a `RecordingSink` for tests.
- `Injector`: `Key(mods, usage)`, `Mouse(btn, dx, dy)`, `ReleaseAll()`, `CapsLock()`, using the same state machine and tables as `inject.py`.
- **Tests:** all 8 `test_inject.py` cases, plus a `sizeof(INPUT)` check (40 on x64).

### Task 4: Device discovery and transport (Core)
- `UsbId.TryParse(pnpDeviceId)`.
- `DeviceScanner.Scan() -> IReadOnlyList<DeviceCandidate(Port, BoardKind, Vid, Pid)>` via WMI `Win32_PnPEntity`, where the name contains `(COMn)`.
  - Ordering: RP2040 first, then ESP32-C3.
- `ISerialTransport` with `SerialPortTransport`:
  - `System.IO.Ports`;
  - DTR/RTS set **before** `Open` for each board kind;
  - line-based read with a 100 ms timeout.
- **Tests:** VID/PID parsing, board classification and ordering, and a fake transport.

### Task 5: Firmware `VER` command (parent repo, branch `feature/fw-version`)
- **RP2040** (`src/usb_io.c`): `VER` replies `VERSION rp2040-169 <FW_VERSION> <__DATE__>`. `FW_VERSION` is defined in `src/version.h`.
- **ESP32-C3** (`esp32c3/src/link.cpp`): `VERSION esp32c3-128 <FW_VERSION> <build>`.
- Protocol comments and `CLAUDE.md` updated. The parent `.gitignore` change (ignoring `windows-app/`) is committed here too.
- **Tests:** a new board test `VER` → `VERSION` for the RP2040, which is connected. The ESP32 test skips.
- **Done when:** both firmwares build, the RP2040 is flashed, and the tests are green. Merge.

### Task 6: DeviceSession (Core)
- `DeviceSession(ISerialTransport, Injector, ISelectionProvider, IClock)`. Its `RunAsync(ct)` runs this sequence:
  - `HELLO` → expect `PONG` (otherwise, not a Touch Deck);
  - `VER` → `FirmwareInfo`;
  - `TIME`;
  - loop: dispatch lines, heartbeat every 2 s, `LEDS` poll every 250 ms, `TIME` hourly;
  - `finally`: `ReleaseAll`.
- Exposes events `StateChanged`, `Log`, `ClipSent`, `DiagnosticsUpdated`, plus `SendText(string)` and `RebootToBootloader()`.
- `DeviceManager`: poll the scanner every 2 s, run one session at a time, and handle busy ports and unplugging.
- **Tests:** all with a fake transport and fake clock:
  - `COPY` → `CLIP` bytes;
  - `K`/`M` → injector;
  - `LEDS` sent on change only;
  - the transport throwing mid-session → `ReleaseAll` called;
  - `VER` missing → firmware unknown.

### Task 7: Selection provider (Core)
- `ISelectionProvider.Grab() -> (text, src)`. The UI Automation implementation uses the `UIAutomationClient` COM interop: focused element, up to 4 parents, TextPattern selection. The clipboard fallback runs on an STA thread.
- **Tests:** the fallback order with fakes. A smoke test that reading the clipboard works on the current machine.

### Task 8: Settings, clip history, autostart (Core)
- `AppSettings` (JSON in `%APPDATA%\TouchDeck`): dry-run, diagnostics, start with Windows.
- `ClipHistory` (last 10, most recent first, duplicates moved to the top).
- `Autostart` (HKCU `Software\Microsoft\Windows\CurrentVersion\Run`).
- **Tests:** round-trip, history rules, and autostart against a test registry key.

### Task 9: WPF shell and tray (App)
- **Main window:**
  - device card: board, port, firmware, status dot;
  - diagnostics panel;
  - Send-text box;
  - clip history list with Resend;
  - log view;
  - settings toggles;
  - Reboot to bootloader and Update firmware buttons.
- **Tray icon:** status colour, menu (Open, Dry-run, Quit), double-click to open. Closing the window hides it; Quit releases the injector and exits.
- Single instance, enforced with a named mutex.
- **Smoke:** the app starts, finds the connected RP2040, and shows its version. The window title shows the app version.

### Task 10: Firmware update (App + Core)
- `FirmwareUpdater` for the RP2040: `BOOT`, wait for the `RPI-RP2` drive, copy the bundled `firmware/rp2040-169.uf2`, then wait for `CAFE:4011` and check `VER`.
- `firmware/manifest.json` records `{board, version, file}`.
- The UI offers the update when the device's version is lower than the bundled one, or unknown.
- **Tests:** version comparison and drive detection (fakes).
- **Smoke:** a real update of the connected RP2040 with the current firmware.

### Task 11: Installer and build script
- `installer/TouchDeck.Installer.wixproj` (WixToolset.Sdk v5):
  - per-user MSI;
  - harvests the self-contained publish output;
  - Start-menu shortcut;
  - MajorUpgrade;
  - app icon.
- `build.ps1`: test, `dotnet publish` self-contained, copy the firmware in, build the MSI to `out/`.
- **Smoke:**
  - `msiexec /i out\TouchDeck.msi /qn`;
  - check the files and shortcut exist;
  - launch, check the process is alive and the port is taken;
  - quit;
  - `msiexec /x /qn`, and check nothing is left.
  - Finally, reinstall for the user.

### Task 12: Final review and docs
- A fresh reviewer checks the whole branch; fix what it finds.
- `README.md` covers install, build, the SmartScreen note and troubleshooting. `CLAUDE.md` holds the conventions for this repo.
- Merge to `main`.
