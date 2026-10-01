# CLAUDE.md

The macOS companion app for the Touch Deck boards: Swift 5 mode, SwiftUI, macOS 14+, Xcode 16+. It lives in the `macos-app/` folder of the TouchDeck repo, beside `windows-app/`. **Parity with the Windows app is the spec.** When behaviour is unclear, read the matching C# file in `../windows-app/src/` and do the same. The serial protocol is documented at the top of `../src/usb_io.c`. `README.md` covers building, the Mac specifics and signing.

## Commands

```sh
xcodebuild -scheme TouchDeck -configuration Debug build
xcodebuild -scheme TouchDeck test              # pre-action builds ../hostui/out/libtdui_*.dylib
python tools/make_icons.py                      # AppIcon + template menu bar icons
python tools/make_xcodeproj.py                  # only when targets or build settings change
```

- The app holds the board's serial port (opened exclusive). Quit it (menu bar icon → Quit) before `../tools/flash.py` or the pytest board tests.
- Board ports are `/dev/cu.usbmodem*`. The Python tools' `find_board` picks by `VER`, as on Windows.

## Architecture

- **`TouchDeckCore` (framework, no UI)** mirrors `TouchDeck.Core`, folder for folder. Its public API is what the app uses; tests use `@testable import`.
  - `Session/DeviceSession` is single-threaded: `run(shouldStop:)` owns the transport on its own thread, and other threads only queue requests. `run`'s `defer` always calls `injector.releaseAll()`; keep it that way. Lines read while waiting for a handshake reply are held, never dropped. Only CR/LF are trimmed, so TEXT values keep their spaces.
  - `Session/DeviceManager` scans every 2 s on a thread and runs one session at a time. While `requiredBoard` is set (a firmware install), only that model may connect: both RP boards are `CAFE:4011`.
  - `Input/Injector` maps HID usages to **macOS virtual key codes** (kVK), not PS/2 scancodes. The HID GUI modifier maps to Command. `CGEventSink` sets each key event's flags from the modifiers the board holds. `SwitchableSink` releases real held input when dry run turns on mid-paste, and dry-run text never names the key.
  - `Mirror/UiState` is `ui_state_t` as raw bytes with fixed offsets (1264 bytes). A test checks the size against `tdui_state_size()`. **When `src/ui_state.h` grows, update the offsets here**, along with `windows-app/.../UiState.cs` and `tools/tests/tdui_host.py`.
  - `Mirror/NativeUi` dlopens `libtdui_<board>.dylib` from `TOUCHDECK_TDUI_DIR`, then the app's Frameworks folder. A missing library only turns the device view off, which shows the jiggler card.
  - `Updates/` keeps every property `UpdateTests.cs` pins on Windows:
    - the feed must be signed (raw r||s P-256, checked with CryptoKit) before it is parsed;
    - asset URLs are limited to the TouchDeckUpdates release prefix;
    - redirects only go to GitHub https hosts, at most 5;
    - sizes are capped and SHA-256 is checked while streaming;
    - `clearDownloads` removes only its own file names and refuses a symlink.
- **App (`TouchDeck/`).**
  - `AppController` (`@MainActor ObservableObject`) is the port of the Windows `AppController`. Core callbacks arrive on background threads and hop to the main actor.
  - `TouchDeckApp` declares the `MenuBarExtra` (template image `MenuBarIcon`, or `MenuBarIconBad` when the port is busy or the board doesn't answer), the `main` window and `Settings`.
  - `LSUIElement` is on, so there's no Dock icon.
- **Visual language** matches the devices and the Windows app. The palette is in `Theme.swift`. Settings switches use `PillSwitchStyle` (amber when on). A two-way choice such as Esc / F15 uses `ChoiceSwitch`: always amber, with the chosen label lit. Never use radio buttons for it.

## Workflow

Work on feature branches and open a PR to `main` on GitHub (SynexyConsulting/TouchDeck). A protocol change lands in one PR across the firmware, `windows-app/` and `macos-app/`. Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
