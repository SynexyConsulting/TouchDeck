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
| 5 | `feature/round-watch` (newest; this file) | Silent **round watch** as page 1 on the RP2350 and ESP32-C3; ESP32 BOOT button (stopwatch, jiggler scale) |

After #5 is merged, ship one release: firmware `fw-v1.8.0` / app `app-v1.3.0` (or whatever's next). Bump `windows-app/Directory.Build.props` `<Version>` and confirm `FW_VERSION "1.8.0"` in both `version.h`. Then tag; the steps are in README "Releases and updates". Published so far: app **1.2.1** and firmware 1.6.0 (rp2040-169).

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

## Next: the macOS app (`macos-app/`)

- It must be built on macOS (Xcode toolchain). VS Code can edit Swift but can't build a Mac app on Windows. Releases could be built by GitHub Actions macOS runners, but their minutes count 10x on the Team plan.
- **Reuse:**
  - `hostui/build.sh` builds `libtdui_rp2040/esp32c3/rp2350.dylib`, the boards' own page code, for the device mirror. The app passes `ui_state_t` (1264 bytes; layout in `src/ui_state.h`, mirrored in `windows-app/src/TouchDeck.Core/Mirror/UiState.cs` and `tools/tests/tdui_host.py`).
  - The serial protocol is documented at the top of `src/usb_io.c`.
  - The update feed has an `app.macos` slot (`tools/make_updates.py`); it's signed with the pinned ECDSA P-256 key (see `windows-app/CLAUDE.md` "Updates and Settings").
- **Parity with the Windows app:**
  - device mirror (click = `TAP`, drag = `SWIPE`);
  - COPY (selected text / clipboard), PASTE by the board;
  - PC mode for the ESP32: perform `K`/`M` lines, so the app needs Accessibility permission for input injection;
  - Settings: versions, updates, startup, Jiggler section, dry run;
  - new-board install: macOS sees the UF2 drive as a mounted volume, and the 1200-baud reboot works the same way.
- **Serial on macOS:** RP boards need DTR high; the ESP32-C3 needs DTR and RTS low (they're its reset lines).

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
