# Touch Deck for macOS

The Mac companion app for the Touch Deck boards. It does what the Windows app does (`../windows-app/`), with the same protocol, behaviour and wording. The difference: it lives in the **menu bar** (the top-right of the screen), with a monochrome icon and no Dock icon.

> **Status: first pass, not yet built.** It was written on Windows, where Swift can't be compiled. The first job on a Mac is to build it, run the tests and fix what the compiler finds. See `../docs/HANDOFF.md`, "macOS: first steps".

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
| `TouchDeckCore/` | Framework, no UI. A port of `windows-app/src/TouchDeck.Core`: Protocol, Devices (IOKit scan, termios serial), Input (HID → kVK codes, CGEvent), Selection (Accessibility API, then the pasteboard), Session, Mirror (`ui_state_t` bytes, dlopen'd renderers), Jiggler, Updates (CryptoKit-verified feed), Firmware (UF2, onboarding), App (settings, login item) |
| `TouchDeck/` | The SwiftUI app: `MenuBarExtra`, the main window (device mirror on the left), Settings (⌘,), and `AppController`, the port of the Windows `AppController` |
| `TouchDeckTests/` | XCTest. Ports of the Windows unit tests, with fake transport, keyboard, selection and clock |
| `scripts/bundle-native.sh` | The app build phase described above |
| `tools/make_icons.py` | Draws `AppIcon` and the template `MenuBarIcon` / `MenuBarIconBad` (`python macos-app/tools/make_icons.py`) |
| `tools/make_xcodeproj.py` | Writes `TouchDeck.xcodeproj` |

## Mac specifics

- **Serial ports.** Boards appear as `/dev/cu.usbmodem*`, found through IOKit by USB ID (RP boards `CAFE:4011`, ESP32-C3 `303A:1001`). Ports are opened exclusive (`TIOCEXCL`), so a second program gets "port busy". The modem lines are set in one `TIOCMSET`: DTR high for the RP boards, DTR and RTS low for the ESP32-C3, where they are its reset lines. `HUPCL` is cleared so closing the port doesn't reset the C3.
- **Accessibility permission** is needed for two things: posting the ESP32-C3's PC-mode keys and mouse (CGEvent), and reading the selected text for COPY. Without it, COPY falls back to the clipboard and PC-mode input is dropped. The app asks at first start; Settings shows the state and links to System Settings. With "Sign to Run Locally", macOS forgets the grant on every rebuild, which is another reason to set the team early.
- **Hotkey:** ⌃⌥C (Control-Option-C) sends the selection, the Mac version of Ctrl+Alt+C. It uses Carbon `RegisterEventHotKey`, which needs no permission.
- **Launch at login:** `SMAppService.mainApp`. There's no "start minimized" setting; a menu bar app starts without a window.
- **Firmware install:** the UF2 bootloader mounts as `/Volumes/RPI-RP2` or `/Volumes/RP2350`. The image is written with a plain data write, not a Finder-style copy, which adds `._` files and extended attributes. Stock Pico programs are rebooted into the bootloader by opening their port at 1200 baud, as on Windows.
- **Updates:** the same signed feed as Windows (`updates.json` + `.sig`, pinned P-256 key), reading `app.macos`. The Mac app ships as a signed, notarized `.pkg`. The verified package opens in Installer and the app quits. `tools/make_updates.py` doesn't write `app.macos` yet; add that with the first Mac release.

## Signing (Apple Developer account)

The project builds unsigned-for-distribution ("Sign to Run Locally", `CODE_SIGN_IDENTITY = -`) so the first build needs no account. To connect it to your account:

1. In Xcode, go to Settings > Accounts and add your Apple ID.
2. Select the TouchDeck project, then each target (TouchDeck, TouchDeckCore, TouchDeckTests). Under Signing & Capabilities, keep "Automatically manage signing" on and choose your Team. Xcode writes `DEVELOPMENT_TEAM` into the project. Team IDs aren't secret, so committing it is fine. Accessibility grants now survive rebuilds.
3. **To distribute outside the App Store:**
   - Archive (Product > Archive), then use Distribute App > Developer ID. Xcode signs with your "Developer ID Application" certificate and the hardened runtime (on for Release), then notarizes and staples.
   - For the `.pkg` the updater expects, use `productbuild --sign "Developer ID Installer: …"` and notarize the package with `xcrun notarytool submit … --wait`, then `xcrun stapler staple`.
   - A CI version of this needs the certificates and an App Store Connect API key as secrets. Those are notes for later, and nothing secret goes in this repo.

The app isn't sandboxed (serial ports, input injection, other apps' selections), so it can't go in the Mac App Store as it is. Developer ID is the route.
