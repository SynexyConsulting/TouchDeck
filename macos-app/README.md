# Touch Deck for macOS

The Mac companion app for the Touch Deck boards. It does what the Windows app does (`../windows-app/`), with the same protocol, behaviour and wording. The difference: it lives in the **menu bar** (the top-right of the screen), with a monochrome icon and no Dock icon.

> **Status: builds and runs (Xcode 27, macOS 27).** The unit tests pass and the app has run against an RP2350 round board: mirror, taps, sending text, smoke snapshots. COPY's selection works once Accessibility is granted. Still to try by hand: ESP32-C3 PC mode, firmware install, and signing. See `../docs/HANDOFF.md`.

## Requirements

- macOS 14 (Sonoma) or later, on Apple silicon or Intel.
- Xcode 16 or later. The project uses folder-synchronized groups, so every file in `TouchDeck/`, `TouchDeckCore/` and `TouchDeckTests/` is in its target automatically.

## Build and test

```sh
open macos-app/TouchDeck.xcodeproj           # then Product > Run (⌘R) or Test (⌘U)
# or from a terminal:
cd macos-app
xcodebuild -scheme TouchDeck -configuration Debug build
xcodebuild -scheme TouchDeck test
```

- **Test runs build the renderers first.** The scheme's test pre-action runs `../hostui/build.sh`, which builds the boards' own page code into `hostui/out/libtdui_*.dylib`. `TOUCHDECK_TDUI_DIR` points the mirror tests at that folder. Without the libraries, those tests skip.
- **App builds bundle the renderers and firmware.** The app's build phase `scripts/bundle-native.sh` builds the same libraries into `Touch Deck.app/Contents/Frameworks` and signs them. It also copies `../build/watch.uf2` and `../build-rp2350/deck128.uf2`, if they've been built, into `Contents/Resources/firmware` with a `manifest.json` that carries `FW_VERSION`.
- **If the project file won't open:** run `brew install xcodegen && xcodegen` in this folder. It regenerates the project from `project.yml`. `tools/make_xcodeproj.py` writes the committed project; keep the two in step.

## Layout

| Folder | What it is |
|---|---|
| `TouchDeckCore/` | Framework, no UI. A port of `windows-app/src/TouchDeck.Core`: Protocol, Devices (IOKit scan, termios serial), Input (HID → kVK codes, CGEvent), Selection (Accessibility API, then ⌘C, then the pasteboard), Session, Mirror (`ui_state_t` bytes, dlopen'd renderers), Jiggler, Updates (CryptoKit-verified feed), Firmware (UF2, onboarding), App (settings, login item) |
| `TouchDeck/` | The SwiftUI app: `MenuBarExtra`, the main window (device mirror on the left), Settings (⌘,), and `AppController`, the port of the Windows `AppController` |
| `TouchDeckTests/` | XCTest. Ports of the Windows unit tests, with fake transport, keyboard, selection and clock |
| `scripts/bundle-native.sh` | The app build phase described above |
| `tools/make_icons.py` | Draws `AppIcon` and the template `MenuBarIcon` / `MenuBarIconIdle` / `MenuBarIconBad` (`python macos-app/tools/make_icons.py`) |
| `TouchDeck/Fonts/` | Barlow and JetBrains Mono (OFL), as the Windows app embeds them; registered at launch |
| `tools/make_xcodeproj.py` | Writes `TouchDeck.xcodeproj` |

## Mac specifics

- **Serial ports.** Boards appear as `/dev/cu.usbmodem*`, found through IOKit by USB ID (RP boards `CAFE:4011`, ESP32-C3 `303A:1001`). Ports are opened exclusive (`TIOCEXCL`), so a second program gets "port busy". The modem lines are set in one `TIOCMSET`: DTR high for the RP boards, DTR and RTS low for the ESP32-C3, where they are its reset lines. `HUPCL` is cleared so closing the port doesn't reset the C3.
- **Accessibility permission** is needed for two things: posting the ESP32-C3's PC-mode keys and mouse (CGEvent), and reading the selected text for COPY. Without it, COPY falls back to the clipboard and PC-mode input is dropped. The app asks at first start; Settings shows the state and links to System Settings. With "Sign to Run Locally", macOS forgets the grant on every rebuild, which is another reason to set the team early.
- **COPY reads the app you were using.** The selection comes from the frontmost app, or from the last app you used when Touch Deck itself is in front (you clicked the mirror). It walks up to 4 parent elements, as Windows does, and asks Chromium and Electron apps for their accessibility tree (`AXManualAccessibility`). When Accessibility can't tell (the app doesn't expose its text), the app sends ⌘C, takes the copied text and puts the previous clipboard back. When Accessibility says nothing is selected, it doesn't send ⌘C (some editors copy the whole line), and the clipboard is used as on Windows.
- **Hotkey:** ⌃⌥C (Control-Option-C) sends the selection, the Mac version of Ctrl+Alt+C. It uses Carbon `RegisterEventHotKey`, which needs no permission.
- **Launch at login:** `SMAppService.mainApp`. There's no "start minimized" setting; a menu bar app starts without a window.
- **Firmware install:** the UF2 bootloader mounts as `/Volumes/RPI-RP2` or `/Volumes/RP2350`. The image is written with a plain data write, not a Finder-style copy, which adds `._` files and extended attributes. Stock Pico programs are rebooted into the bootloader by opening their port at 1200 baud, as on Windows.
- **Firmware copy** is forced out to the drive (`F_FULLFSYNC`): macOS can otherwise keep FAT writes cached, and the board only reboots once it has every block.
- **Updates:** the same signed feed as Windows (`updates.json` + `.sig`, pinned P-256 key), reading `app.macos`. The Mac app ships as a signed, notarized `.pkg`. The verified package opens in Installer and the app quits; a detached shell reopens Touch Deck once Installer closes. Publish it with `tools/publish_release.py --app-macos TouchDeck-X.Y.Z.pkg --app-macos-version X.Y.Z` (the Mac app has its own version line).
- **One copy at a time.** A second launch brings the running one forward. `"Touch Deck.app/Contents/MacOS/Touch Deck" --quit` closes it (frees the serial port for `flash.py` and the board tests), as `TouchDeck.exe --quit` does on Windows.
- **Smoke snapshots:** `"Touch Deck.app/Contents/MacOS/Touch Deck" --smoke DIR [--smoke-steps] [--smoke-check-updates]` waits for a board, writes `smoke.png` (the window), `mirror.png` (the device view at 1:1), `settings.png` and `smoke.txt`, and quits. `--smoke-steps` drives the board (jiggler page, `ANIM 1`, a sent clip) and saves the view after each step.

## Signing (Apple Developer account)

The project builds unsigned-for-distribution ("Sign to Run Locally", `CODE_SIGN_IDENTITY = -`) so the first build needs no account. To connect it to your account:

1. In Xcode, go to Settings > Accounts and add your Apple ID.
2. Select the TouchDeck project, then each target (TouchDeck, TouchDeckCore, TouchDeckTests). Under Signing & Capabilities, keep "Automatically manage signing" on and choose your Team. Xcode writes `DEVELOPMENT_TEAM` into the project. Team IDs aren't secret, so committing it is fine. Accessibility grants now survive rebuilds.
3. **To distribute outside the App Store:**
   - Archive (Product > Archive), then use Distribute App > Developer ID. Xcode signs with your "Developer ID Application" certificate and the hardened runtime (on for Release), then notarizes and staples.
   - For the `.pkg` the updater expects, use `productbuild --sign "Developer ID Installer: …"` and notarize the package with `xcrun notarytool submit … --wait`, then `xcrun stapler staple`.
   - A CI version of this needs the certificates and an App Store Connect API key as secrets. Those are notes for later, and nothing secret goes in this repo.

The app isn't sandboxed (serial ports, input injection, other apps' selections), so it can't go in the Mac App Store as it is. Developer ID is the route.
