# CLAUDE.md

The macOS companion app for the Touch Deck boards: Swift 5 mode, SwiftUI, macOS 14+, Xcode 16+. It lives in the `macos-app/` folder of the TouchDeck repo, beside `windows-app/`. **Parity with the Windows app is the spec.** When behaviour is unclear, read the matching C# file in `../windows-app/src/` and do the same. The serial protocol is documented at the top of `../src/usb_io.c`. `README.md` covers building, the Mac specifics and signing.

## Commands

```sh
xcodebuild -scheme TouchDeck -configuration Debug build
xcodebuild -scheme TouchDeck test              # pre-action builds ../hostui/out/libtdui_*.dylib
python tools/make_icons.py                      # AppIcon + template menu bar icons
python tools/make_xcodeproj.py                  # only when targets or build settings change
```

- The app holds the board's serial port (opened exclusive). Quit it (menu bar icon → Quit, or run the binary with `--quit`) before `../tools/flash.py` or the pytest board tests.
- `"Touch Deck.app/Contents/MacOS/Touch Deck" --smoke DIR [--smoke-steps] [--smoke-check-updates]` writes window/mirror/settings PNGs and `smoke.txt`, then quits: the way to check the UI from a terminal (`screencapture` needs a permission the terminal may not have).
- Xcode exports every platform's deployment target to scheme actions, so the test pre-action runs `build.sh` under `env -i`; keep that if you touch it.
- Board ports are `/dev/cu.usbmodem*`. The Python tools' `find_board` picks by `VER`, as on Windows.

## Architecture

- **`TouchDeckCore` (framework, no UI)** mirrors `TouchDeck.Core`, folder for folder. Its public API is what the app uses; tests use `@testable import`.
  - `Session/DeviceSession` is single-threaded: `run(shouldStop:)` owns the transport on its own thread, and other threads only queue requests. `run`'s `defer` always calls `injector.releaseAll()`; keep it that way. Lines read while waiting for a handshake reply are held, never dropped. Only CR/LF are trimmed, so TEXT values keep their spaces.
  - `Session/DeviceManager` scans every 2 s on a thread and keeps **one slot per port**, each with its own session (several boards at once; spec `../docs/superpowers/specs/2026-10-03-multi-board-design.md`). It calls `onSlotChanged`/`onSlotRemoved`/`onSessionStarted` per port on background threads; `links`, `sessions` and `session(for:)` read it. An install recognises its board as a *new* session reporting the target model.
  - `App/Boards.swift`: `LogEntry` (board-tagged log lines), `BoardSelection` (which tab is selected) and `BoardLabels` (tab text, short `/dev/cu.` ports), as in `Boards.cs`.
  - `Input/Injector` maps HID usages to **macOS virtual key codes** (kVK), not PS/2 scancodes. The HID GUI modifier maps to Command. `CGEventSink` sets each key event's flags from the modifiers the board holds. `SwitchableSink` releases real held input when dry run turns on mid-paste, and dry-run text never names the key.
  - `Mirror/UiState` is `ui_state_t` as raw bytes with fixed offsets (1264 bytes). A test checks the size against `tdui_state_size()`. **When `src/ui_state.h` grows, update the offsets here**, along with `windows-app/.../UiState.cs` and `tools/tests/tdui_host.py`.
  - `Selection/` reads COPY's text from the app in use (`TargetApp`: the frontmost app, or the last one when Touch Deck is in front): Accessibility first, then ⌘C with the clipboard restored (`CopyCommandSelection`, only when Accessibility can't tell), then the clipboard.
  - `Mirror/NativeUi` dlopens `libtdui_<board>.dylib` from `TOUCHDECK_TDUI_DIR`, then the app's Frameworks folder. A missing library only turns the device view off, which shows the jiggler card.
  - `Updates/` keeps every property `UpdateTests.cs` pins on Windows:
    - the feed must be signed (raw r||s P-256, checked with CryptoKit) before it is parsed;
    - asset URLs are limited to the TouchDeckUpdates release prefix;
    - redirects only go to GitHub https hosts, at most 5;
    - sizes are capped and SHA-256 is checked while streaming;
    - `clearDownloads` removes only its own file names and refuses a symlink.
- **App (`TouchDeck/`).**
  - `AppController` (`@MainActor ObservableObject`) is the port of the Windows `AppController`: the app-wide side plus `boards` and `selected`. Core callbacks arrive on background threads and hop to the main actor.
  - `BoardController` (`@MainActor ObservableObject`) is one tab, as `BoardController.cs`. **Views observe the selected board directly** (`MainBody`, `SettingsBody`, `DeviceMirrorView`, `JigglerCard` take `@ObservedObject var board`): `@Published boards` doesn't pass on changes inside a board. The tab strip (`BoardTab` pills) and the log's "Only selected board" switch appear with two or more boards.
  - `TouchDeckApp` declares the `MenuBarExtra` (template image `MenuBarIcon` when connected, `MenuBarIconIdle` while looking, `MenuBarIconBad` when the port is busy or the board doesn't answer), the `main` window and `Settings`. `AppDelegate` keeps one copy running (a second launch posts a distributed notification and exits; `--quit` asks the running copy to quit) and shows the window on reopen or a clicked notification.
  - Text uses `Theme.ui/head/mono` (Barlow, JetBrains Mono from `Fonts/`, system fonts as fallback).
  - `LSUIElement` is on, so there's no Dock icon.
- **Visual language** matches the devices and the Windows app. The palette is in `Theme.swift`. Settings switches use `PillSwitchStyle` (amber when on). A two-way choice such as Esc / F15 uses `ChoiceSwitch`: always amber, with the chosen label lit. Never use radio buttons for it.

## Workflow

Work on feature branches and open a PR to `main` on GitHub (SynexyConsulting/TouchDeck). A protocol change lands in one PR across the firmware, `windows-app/` and `macos-app/`. Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
