# Handoff: where Touch Deck stands (2026-10-01)

Read this first in a new session, together with `CLAUDE.md`, `windows-app/CLAUDE.md` and `docs/superpowers/` (specs and plans). It records what a fresh session can't see: what's merged, what's pending, and decisions made in conversation.

## Branches and merge order

None of these branches has been merged or released yet. They are **stacked**, and a stacked PR merges into its *base* branch, not into main. So merge them in this order, and open each PR against `main` only after the one before it is in:

| # | Branch / PR | What it adds |
|---|---|---|
| 1 | **PR #8** `fix/ci-arm-toolchain` | CI builds RP firmware with the pinned Arm GNU 14.2.Rel1 (Ubuntu's GCC 13 drew 10-30% slower) |
| 2 | **PR #9** `feature/device-mirror` (draft) | App shows a live device-shaped mirror (firmware 1.7.0): page code in `ui_pages.c`, `hostui/` DLLs, `STATE/TEXT/CLIPTEXT` sync, `FBCRC` |
| 3 | `feature/rp2350-round` | Waveshare **RP2350-Touch-LCD-1.28** (`rp2350-128`), app finds new boards and installs firmware (chip + `TDBOARD:` model check), CI builds both RP firmwares |
| 4 | `feature/jiggler-settings` | **Jiggler menu** panel (cog on the Jiggler page, X closes): context menu on/off, ESC/F15, menu open and pause 0-60 s; `JIG CFG`; settings v4; app Settings → Jiggler; F15 everywhere; firmware **1.8.0** |
| 5 | `feature/round-watch` | Silent **round watch** as page 1 on the RP2350 and ESP32-C3; ESP32 BOOT button (stopwatch, jiggler scale); app Settings: Esc / F15 is an amber switch, not radios |
| 6 | `feature/macos-app` (newest; this file) | **macOS app, first pass, never compiled**: Xcode project, menu bar app, Core ported from Windows, XCTests. See "macOS: first steps" below |

After #5 is merged, ship one release (#6 doesn't change the firmware or the Windows app): firmware `fw-v1.8.0` / app `app-v1.3.0` (or whatever's next). Bump `windows-app/Directory.Build.props` `<Version>` and confirm `FW_VERSION "1.8.0"` in both `version.h`. Then tag; the steps are in README "Releases and updates". Published so far: app **1.2.1** and firmware 1.6.0 (rp2040-169).

## Hardware status (all on firmware 1.8.0 from `feature/round-watch`)

- **RP2040-Touch-LCD-1.69 (COM6):**
  - Every board and mirror test passes. It runs the jiggler-settings build; round-watch changes don't affect this board.
  - The owner checked the Jiggler menu panel, the app Settings section, and the X (after the top-band touch fix).
  - Owner's settings: muted, scale 1.5x, menu on, **F15**, open 2 s, pause 0.
- **RP2350-Touch-LCD-1.28 (COM11):** 20 board tests plus mirror tests pass; settings survive a UF2 install; the owner checked the round watch, the panel and the cog position. The factory demo is backed up at `%USERPROFILE%\.touchdeck\backups\rp2350-touch-lcd-1.28-factory.uf2` (on the Windows PC).
- **ESP32-2424S012C (COM7):** 26 board tests pass; the owner checked the watch (hands appear once the app sends `TIME`) and the BOOT stopwatch.
  - **Pending: F15 over Bluetooth.** The report map was widened from 0x65 to 0x73, and Windows may have cached the old one for the bonded board. If so, remove and re-pair.
  - Testing it sends real keys and right-clicks to the PC over BT, so **only test while the owner is away from the keyboard**.

## Open questions for the owner

- **"Touch Deck HID":** the owner asked for an app called this. Its meaning is unclear: renaming the app, a separate input-only app, or the name for PC mode? Ask before building anything.
- **Code signing:** the Windows MSI is unsigned (no Authenticode). A macOS app needs an Apple Developer account ($99/yr) for signing and notarization outside the App Store.

## macOS: first steps (start here on the Mac)

`macos-app/` was written on Windows, where Swift can't be compiled, so **it has never been built**. Expect compile errors. The plan for the first Mac session is build, test, repair, rebuild, and then try it with the boards. Read `macos-app/README.md` and `macos-app/CLAUDE.md` first.

1. **Tools.**
   - Xcode 16+ (it must open objectVersion 77 projects). Run `xcode-select -p` to check it's selected.
   - Claude Code.
   - `brew install xcodegen` is optional, only needed if the project file won't open.
2. **Open and build.** Run `open macos-app/TouchDeck.xcodeproj`. If Xcode rejects the project, run `cd macos-app && xcodegen` (it reads `project.yml`), then fix `tools/make_xcodeproj.py` to match what worked. Then build:
   `cd macos-app && xcodebuild -scheme TouchDeck -configuration Debug build 2>&1 | tail -50`
3. **Fix compile errors, Core first.**
   - `TouchDeckCore` has no UI and is the bulk of the code, so get it compiling before the app.
   - Likely trouble spots:
     - Swift 6 concurrency warnings (the project is in Swift 5 mode on purpose);
     - `NSLock.withLock`;
     - IOKit `IORegistryEntrySearchCFProperty` casts;
     - `ioctl` overloads in `SerialTransport.swift`;
     - `AppController.init` touching `self.sink` before `manager` is set;
     - `@MainActor` hops in `AppController.hookSession`;
     - `SettingsView` closures.
   - Fix the code, not the tests, unless a test is plainly wrong. The tests are ports of the Windows ones.
4. **Test.** `xcodebuild -scheme TouchDeck test`. The pre-action builds `hostui/out/libtdui_*.dylib` with the system `cc`, the first real run of `hostui/build.sh` on a Mac. `MirrorTests.testRenderersAgreeWithTheLayout` checks the 1264-byte layout against them. The manager tests take about 2 s (a 1.5 s handshake timeout).
5. **Run** (⌘R).
   - The menu bar icon appears, with no Dock icon.
   - Grant Accessibility when asked. With ad-hoc signing the grant resets on each rebuild; setting the team (step 7) fixes that.
   - Plug in each board and check:
     - it connects (`/dev/cu.usbmodem*`);
     - the device mirror draws and taps and swipes work;
     - COPY sends the selection;
     - ⌃⌥C works;
     - the Jiggler settings work, including the Esc/F15 switch.
   - **ESP32-C3:** opening the port must not reset it (DTR/RTS low). PC mode types through CGEvent, so try it in a scratch text editor, not in Claude Code's terminal (Esc interrupts it).
   - **Firmware install:** a stock or bootloader RP2350 or RP2040 gets the bundled UF2 (build `build/watch.uf2` and `build-rp2350/deck128.uf2` on the Mac first, or copy them over).
6. **Board tests from the Mac.** The pytest board tests use `/dev/cu.*` ports through `tools/touchdeck.py`. Check `find_board` and the DTR handling work on macOS, and quit the app before running them.
7. **Signing** (the owner has an Apple Developer account): set the Team on all three targets (README "Signing"). Later: a Developer ID archive and a notarized `.pkg`, an `app.macos` entry in `tools/make_updates.py`, and maybe a macOS job in `release.yml` (macOS runner minutes count 10x on the Team plan).

Not ported yet (all small): the Windows `--smoke` snapshot mode, the activity log's "copy" affordances, and the embedded Barlow/JetBrains Mono fonts (the Mac app uses system fonts for now).

## Deferred minor findings (from final reviews; none block a release)

`feature/rp2350-round`:
- An already-present same-chip bootloader drive wins over the stock board named in the install offer.
- `RebootToBootloader`'s failure (port busy) isn't logged, so the user waits 15 s for "hold BOOT".
- Any stock Pico or Pico 2 program is offered "Install Touch Deck"; the text doesn't say it replaces the program.
- `CheckNewBoards` only catches `ManagementException`; other WMI errors hit the dispatcher backstop every 2 s.
- `Uf2.Inspect` ignores `blockNo`/`numBlocks` and duplicate addresses.
- `find_board` skips busy ports as "not connected" instead of "port busy".
- `flash.py` doesn't check a positional UF2 against `--board`.
- `build.ps1` may bundle a stale `build-rp2350` UF2 under the current `FW_VERSION`.

`feature/jiggler-settings`:
- `settings_save()` clears `save_due` before a possibly failing `flash_safe_execute`, so the change is lost until the next one.
- The app's `JigNote` isn't raised on an `IsConnected` change, so a 1.7.0 board leaves stale text.
- Two fast +/- clicks in the app before the STATE echo send the same value.
- BOOT with the panel open still cycles the hidden scale on the RP boards (the ESP32 now ignores it).
- `near_box_top()` also applies on the round RP2350 (harmless); it could be gated with `#ifndef TD_ROUND`.
- `app.jig_cfg` is read by core1 without atomicity, so at most one torn frame.
- App 1.2.x mirroring a 1.8.0 round board shows the BT page while the panel is open.
- Doc nits: the jiggler-settings spec's App/Tests sections still mention page counts from the swipe-page design.

## Things that bit us (also in the second brain on the Windows PC)

- **Hook-based input tests:** `hook_menu`-style tests swallow *all* right-clicks and ESC, the owner's included, while they run. And the board's ESC hits whatever window has focus, including Claude Code's terminal, where ESC interrupts the run. Run them only while the owner is idle.
- **Port holders:** only one program can hold a board's serial port, and the Windows app is the usual holder. Quit it (`TouchDeck.exe --quit`) before `flash.py` or board tests.
- **Backslashes:** any source containing a backslash must go through the Edit/Write tools or a script file, never a shell heredoc.
- **Two RP boards, one USB ID:** both are `CAFE:4011`. Pick by `VER` model (`touchdeck.find_board`), never "first port".
- **The 1.69's touch top band:** the panel squeezes its top band (a finger at y 42 reads y ≈ 2), so top targets use `near_box_top()`.
- **RP2350 flash:** SDK 2.x RP2350 UF2s write an E10 workaround block at the top of flash, so settings live in the second-to-last sector.
- **ESP32 download mode:** after entering it by hand (BOOT held), the ESP32 can stay there after flashing. Unplug and replug.
