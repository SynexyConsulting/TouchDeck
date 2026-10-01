# RP2350 Round Board and New-Board Onboarding: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Touch Deck runs on the Waveshare RP2350-Touch-LCD-1.28 (`rp2350-128`). The Windows app finds a board without Touch Deck firmware and installs the right model's firmware, checked by both chip and model.

**Architecture:**
- **One RP firmware tree (`src/`), two CMake configurations.** `TD_BOARD=rp2040_169` (default) builds `watch` into `build/`. `TD_BOARD=rp2350_128` builds `deck128` into `build-rp2350/`.
- **Layout switches:** layout-specific headers in `src/` switch on `TD_ROUND`. The round layout lives in `src/round/` as identical copies of the ESP32's round pages, geometry and jiggler paths, with a pytest that keeps them identical.
- **New driver:** a GC9A01 LCD driver sits behind the existing `lcd.h`.
- **App:**
  - learns the Raspberry Pi USB IDs (bootloader and stock SDK programs);
  - reboots stock programs at 1200 baud;
  - validates UF2 chip family and an embedded `TDBOARD:<model>;` marker;
  - bundles one UF2 per model.

**Tech Stack:** Pico SDK 2.1.1 (RP2040 and RP2350-ARM-S), Arm GNU 14.2.Rel1, C#/.NET 8 WPF, pytest, xUnit, MSVC for `hostui`.

**Spec:** `docs/superpowers/specs/2026-09-29-rp2350-round-and-board-onboarding-design.md`

## Global Constraints

- **Branch:** `feature/rp2350-round` sits on `feature/device-mirror` (PR #9). Don't open its PR until #9 is merged. Then open it against `main` (stacked PRs merge into their base).
- **The RP2040 1.69 build stays behaviourally identical.** Its board tests (`tools/tests/test_board_rp2040.py`) must pass on hardware after every firmware task that touches `src/`.
- **Never read the CST816 on a timer.** The touch driver is shared as is.
- **Generated files:** never hand-edit `jig_paths.*`, `aa_fonts.*` or `JigPaths.g.cs`. Change `tools/jigpaths.py` or `tools/fontgen.py` and regenerate with `python` (3.9).
- **Protocol changes** go in `usb_io.c`, `esp32c3/src/link.cpp` and the app's `Protocol/` together. This plan adds none.
- **Installs on the new board:** during development, flash it with `picotool`. The acceptance test restores `%USERPROFILE%\.touchdeck\backups\rp2350-touch-lcd-1.28-factory.uf2` and installs through the app.
- **Versions:** firmware and app version numbers are bumped only at release time, not in this plan. `rp2350-128` reports the current `FW_VERSION`.
- **Backslashes:** any source containing a backslash goes through the Edit or Write tool, never a heredoc.

## Review Focus

1. **Two boards on one PC:** both RP boards enumerate as `CAFE:4011`. Tools, tests and the app must pick a port by the model `VER` reports, never by "the first CAFE port". Flashing must target the chosen board.
2. **Marker split across UF2 blocks:** a `TDBOARD:` marker split across two 256-byte UF2 blocks must still be found. The model check reassembles the payload by address, and a unit test builds a UF2 with the marker split.
3. **The RP2350's own UF2 layout:** the SDK 2.x UF2 starts with an "absolute" family block (`0xE48BFF57`) before the ARM-S blocks (`0xE48BFF59`). Validation must accept that mix and reject RP2040 blocks in an RP2350 image.
4. **Stock firmware that ignores 1200 baud:** the app must time out (5 s) and tell the user to hold BOOT, not hang.
5. **The bootloader drive's name:** the RP2350's drive label and `INFO_UF2.TXT` differ from the RP2040's (`RP2350`, not `RPI-RP2`). Confirm on the board, and pin the parser with the real file contents.

---

### Task 1: Build selection, layout switches, and the round layout copies

**Files:**
- Modify: `CMakeLists.txt`.
- Modify (switch at top): `src/board.h`, `src/ui.h`, `src/ui_pages.h`, `src/version.h`.
- Modify: `tools/jigpaths.py`, which also writes `src/round/jig_paths.{h,c}` and puts the `TD_ROUND` switch into `src/jig_paths.h`. Regenerate.
- Create: `src/round/ui.h`, `src/round/ui_pages.h`, `src/round/ui_pages.c`, as byte copies of `esp32c3/src/`.
- Create: `src/boards/rp2350_128.h`.
- Test: `tools/tests/test_shared_copies.py`, extending the existing identical-files test if present (grep `identical` in `tools/tests`).

**Interfaces:**
- Produces:
  - compile definitions `TD_BOARD_RP2350_128` and `TD_ROUND`;
  - `FW_BOARD` = `"rp2350-128"` under that board;
  - `LCD_W`/`LCD_H` = 240/240 under round;
  - round `SCR_CLIP`=0, `SCR_JIG`=1, and `UI_PAGE_COUNT` (2 on USB-only round, else `SCR_COUNT`).

- [ ] **Step 1: Failing test.** In `tools/tests/test_shared_copies.py`, assert byte equality for these pairs: `esp32c3/src/ui_pages.c` with `src/round/ui_pages.c`, `ui_pages.h`, `ui.h`, `jig_paths.c`, `jig_paths.h`.
- [ ] **Step 2:** `python -m pytest tools/tests/test_shared_copies.py -q`. Expected: FAIL (files missing).
- [ ] **Step 3: Copy, generate and switch.**
  - `jigpaths.py`: add the output dir `src/round` with the esp32c3 layout. Template `src/jig_paths.h` to start with `#ifdef TD_ROUND` / `#include "round/jig_paths.h"` / `#else`, ending with `#endif`.
  - `src/ui.h`, `src/ui_pages.h`: the same switch to `round/…`.
  - `src/board.h`: `#ifdef TD_BOARD_RP2350_128` `#include "boards/rp2350_128.h"` `#else` (existing body) `#endif`.
  - `boards/rp2350_128.h`: the same pins as 1.69 (LCD 8/9/10/11/13/25 on spi1, I2C 6/7, TP 21/22, BAT 29), `LCD_W 240`, `LCD_H 240`, no `BUZZER_PIN`, no `SYS_EN_PIN`, no RTC.
  - `src/version.h`: `#ifdef TD_BOARD_RP2350_128` `#define FW_BOARD "rp2350-128"` `#else` `#define FW_BOARD "rp2040-169"` `#endif`.
  - `src/round/ui_pages.h` and `esp32c3/src/ui_pages.h`, identically: after the enum, add

    ```c
    #ifdef UI_USB_ONLY
    #define UI_PAGE_COUNT 2
    #else
    #define UI_PAGE_COUNT SCR_COUNT
    #endif
    ```

  - Chip label in `src/round/ui_pages.c` and `esp32c3/src/ui_pages.c`, identically: `const char *label = bt ? "BLUETOOTH" : UI_PC_LABEL;` with `#ifndef UI_PC_LABEL` / `#define UI_PC_LABEL "PC"` / `#endif` at the top. The round RP build defines `UI_PC_LABEL="USB"` and `UI_USB_ONLY`.
- [ ] **Step 4: CMake.**
  - `set(TD_BOARD rp2040_169 CACHE STRING "rp2040_169 | rp2350_128")` before `pico_sdk_import`.
  - For `rp2350_128`: set `PICO_PLATFORM rp2350-arm-s` and `PICO_BOARD waveshare_rp2350_touch_lcd_1.28`, and make target `deck128`.
  - `deck128` sources: the shared `src/*.c` minus `lcd.c`, `buzzer.c`, `ui_pages.c`, `jig_paths.c`, plus `src/lcd_gc9a01.c` (Task 2), `src/round/ui_pages.c` and `src/round/jig_paths.c`.
  - `deck128` definitions: `TD_BOARD_RP2350_128 TD_ROUND UI_USB_ONLY UI_PC_LABEL="USB"`.
  - Otherwise the `watch` target is unchanged.
- [ ] **Step 5: Verify.**
  - pytest passes.
  - `ninja -C build` (RP2040) builds with no warnings.
  - `python tools/jigpaths.py` leaves `git diff` empty in `esp32c3/` and `windows-app/`.
  - `cd esp32c3 && python -m platformio run` succeeds.
- [ ] **Step 6: RP2040 regression.**
  - Flash `build/watch.uf2` to the 1.69, when it's connected.
  - Run `python -m pytest tools/tests/test_board_rp2040.py -q`. Expected: 15 passed, or skipped if the board is absent. If skipped, note it in the ledger and re-run when it's connected.
- [ ] **Step 7: Commit** "Firmware: board selection (TD_BOARD) and the round layout for RP boards".

### Task 2: GC9A01 LCD driver and bring-up on the RP2350

**Files:**
- Create: `src/lcd_gc9a01.c`, implementing `src/lcd.h`. The init sequence is copied from Waveshare's `LCD_1in28.c` in the demo zip (scratchpad `ws1.28/demo/.../c/lib/LCD/LCD_1in28.c`); credit it in the file header and in THIRD-PARTY-NOTICES.
- Modify: `src/main.c`, `src/ui.c`, `src/button.c`, `src/usb_io.c` (anything using `SCR_WATCH`, buzzer, stopwatch or `SYS_EN`), guarded by `#ifndef TD_ROUND` / `#ifdef BUZZER_PIN`.

**Interfaces:**
- Consumes Task 1's `LCD_W/H` and pins.
- Produces the same `lcd_init / lcd_set_backlight / lcd_push_frame / lcd_push_rect / lcd_wait` as `lcd.c`, with no row offset (240×240).

- [ ] **Step 1: Driver.** Mirror `lcd.c`:
  - 8-bit SPI for commands;
  - `lcd_push_frame` switches to 16-bit frames and DMAs `fb`;
  - `lcd_wait` restores 8-bit and releases CS;
  - `CASET`/`RASET` without offset;
  - PWM backlight on 25.
- [ ] **Step 2: Round guards.**
  - **main.c:**
    - skip `SYS_EN`;
    - `feedback()` is a no-op without `BUZZER_PIN`;
    - no second alarm, stopwatch or clock tick;
    - `app.screen = SCR_CLIP`;
    - swipe bound `UI_PAGE_COUNT`;
    - `on_touch` for clip/jig is unchanged, because the geometry names are the same in `round/ui.h`;
    - `on_button` jiggler scale only.
  - **ui.c:** no watch-ahead path under `TD_ROUND`; partial regions `ui_dot_rect` plus the status strip as today.
  - **Clock:** keep 200 MHz at 1.15 V on RP2350 too. It's in spec for the RP2350, and it keeps SPI at 50 MHz like the 1.69.
- [ ] **Step 3: BOOT button on RP2350.** Check `pico-examples` `picoboard/button` / the SDK headers for the RP2350 QSPI CS index and `sio_hw->gpio_hi_in` bit (`SIO_GPIO_HI_IN_QSPI_CSN_BITS`). Guard with `#if PICO_RP2040`.
- [ ] **Step 4: Build.**
  ```
  cmake -S . -B build-rp2350 -G Ninja -DCMAKE_BUILD_TYPE=Release -DTD_BOARD=rp2350_128
  ninja -C build-rp2350
  ```
  Expected: `build-rp2350/deck128.uf2`, 0 warnings. `ninja -C build` still builds.
- [ ] **Step 5: Flash the RP2350 (dev).** `picotool load -x build-rp2350/deck128.uf2 -f`, then check:
  - `VER` on the new CAFE port returns `VERSION rp2350-128 …`;
  - `DBG` shows `frames` rising and touch `chip=` non-zero with `fails=0`;
  - `FBCRC 0 0 240 240` answers.
  - **Checkpoint for the owner:** the Clipboard page shows correctly on the round screen, in the right orientation and colours. If the colours are inverted or mirrored, fix `MADCTL`/`INVON` in the init. Ask the owner to look, because it can't be checked any other way.
- [ ] **Step 6:** RP2040 build regression (Task 1 Step 6).
- [ ] **Step 7: Commit** "Firmware: rp2350-128 (GC9A01 round LCD, USB-only round pages)".

### Task 3: Tools and board tests for two RP boards

**Files:**
- Modify: `tools/touchdeck.py`: `find_port(board=None)` asks `VER` on each CAFE port and matches `board`.
- Modify: `tools/flash.py`:
  - `--board rp2040-169|rp2350-128` picks the port by `VER`;
  - the UF2 defaults per board (`build/watch.uf2`, `build-rp2350/deck128.uf2`);
  - a bootloader drive is matched by `INFO_UF2.TXT` `Board-ID` (RPI-RP2 or RP2350).
- Modify: `tools/perf_rp2040.py` → takes `--board`.
- Create: `tools/tests/test_board_rp2350.py`, the RP2040 tests minus watch/mute/stopwatch, with round coordinates from `src/round/ui.h`. It skips when no `rp2350-128` answers.
- Modify: `tools/tests/test_board_rp2040.py`, which selects `rp2040-169` explicitly.

- [ ] **Step 1:** Unit tests for `find_port(board)` with a fake port list (monkeypatch `list_ports` and a fake serial). Expected FAIL, then implement, then PASS.
- [ ] **Step 2:** `test_board_rp2350.py`:
  - swipe L/R between the 2 pages;
  - `TAP` trash (178,42) clears a clip;
  - `JIG ON`/`OFF`;
  - `JIG SCALE`, and the BOOT button cycles the scale on the jiggler page;
  - `WATCH 1` → `STATE` has `page=`;
  - `CLIP CLEAR` ignored while empty;
  - `FBCRC` of both pages equals the host render (see Task 4; mark `xfail` until Task 4 lands, then remove).
- [ ] **Step 3: Run on hardware.**
  - `python -m pytest tools/tests/test_board_rp2350.py -q`: all pass.
  - Perf: `py tools/perf_rp2040.py --board rp2350-128`, 3 runs, recorded in the ledger.
- [ ] **Step 4: Commit** "Tools: pick RP boards by model; rp2350-128 board tests".

### Task 4: Host renderer and app mirror for rp2350-128

**Files:**
- Modify: `hostui/build.bat` and `hostui/build.sh`: add `tdui_rp2350.dll` / `.so` built from `src/round/ui_pages.c` with `-DTD_ROUND -DUI_USB_ONLY -DUI_PC_LABEL="USB"` and `src/` shared C.
- Modify: `windows-app/src/TouchDeck.Core/Mirror/NativeUi.cs` (DLL by board: `rp2350-128` → `tdui_rp2350`), the device shape (round for `rp2350-128`), and `TouchDeck.Core.csproj` (build and copy the third DLL).
- Modify: `tools/mirror_check.py` / host tests to cover `rp2350-128`.

- [ ] **Step 1:** Failing xUnit test: `NativeUi.For("rp2350-128")` loads and renders the Clipboard page. Its CRC equals the board's `FBCRC` captured in Task 3, stored as a fixture from the host render of the same state.
- [ ] **Step 2:** Implement, then `dotnet test`. Expected: all pass.
- [ ] **Step 3:** Remove the Task 3 `xfail` and run `test_board_rp2350.py` on the board. Expected: FBCRC equal on both pages.
- [ ] **Step 4: Commit** "Mirror: rp2350-128 host renderer and round device view".

### Task 5: UF2 validation by chip and model

**Files:**
- Modify: `src/usb_io.c` (or `main.c`): `const char td_board_marker[] __attribute__((used)) = "TDBOARD:" FW_BOARD ";";`, referenced from `VER` so it isn't dropped.
- Modify: `windows-app/src/TouchDeck.Core/Firmware/Firmware.cs` (the `Uf2` class):
  - `Uf2.Inspect(bytes)` returns `{ Chip: Rp2040|Rp2350|Unknown, Board: string?, Valid }`;
  - the payload is reassembled by target address;
  - the chip comes from family IDs (RP2040 `E48BFF56`; RP2350 `E48BFF59`, `E48BFF57` absolute allowed; a mix of RP2040 with RP2350 is invalid);
  - the board comes from the `TDBOARD:` marker.
  - `IsRp2040Image` becomes `Uf2.Inspect(x) is { Chip: Rp2040, Valid: true }` for existing callers.
- Test: `windows-app/tests/TouchDeck.Tests/Uf2Tests.cs`, using real `build/watch.uf2` and `build-rp2350/deck128.uf2` copied to `tests/fixtures/` plus synthetic UF2s: marker split across blocks, wrong family, mixed families, no marker.

- [ ] **Step 1:** Write the tests. Run them. Expected: FAIL (no `Inspect`).
- [ ] **Step 2:** Implement, then run. Expected: PASS. Both firmwares rebuilt with the marker, and the real fixtures report the right board.
- [ ] **Step 3: Commit** "Firmware files carry their model; the app checks chip and model before flashing".

### Task 6: App onboarding (detect, choose model, install)

**Files:**
- Modify: `windows-app/src/TouchDeck.Core/Devices/Devices.cs`:
  - `BoardKind` gains `RpBootloader(chip)` and `RpStock(chip)`;
  - VID/PID map `2E8A:0003`→bootloader RP2040, `2E8A:000F`→bootloader RP2350, `2E8A:000A`→stock RP2040, `2E8A:0009`→stock RP2350.
- Create: `windows-app/src/TouchDeck.Core/Firmware/Onboarding.cs`:
  - `Models.For(chip)`: RP2040 → `["rp2040-169"]`, RP2350 → `["rp2350-128"]`, with display names "Touch LCD 1.69 (rectangle)" and "Touch LCD 1.28 (round)";
  - `InstallAsync(chip, model, bundle, steps, progress, ct)`:
    1. stock: open the COM port at 1200 baud and close (reboot to bootloader), waiting up to 5 s for the drive;
    2. drive: find by `INFO_UF2.TXT` Board-ID `RPI-RP2`/`RP2350` matching the chip;
    3. check the UF2 with `Uf2.Inspect` (chip and model must match);
    4. copy;
    5. wait for `CAFE:4011` whose `VER` board equals the model (30 s).
- Modify: the firmware manifest and bundle to list both boards; `build.ps1` copies `../build-rp2350/deck128.uf2` as `rp2350-128.uf2`, with its version from `src/version.h`.
- Modify: `DeviceManager` reports a `NewBoard(kind, chip, port?)` state.
- App UI (`MainWindow` / `AppController`): a "New board found" card with the model choice (preselected when only one), an **Install Touch Deck** button, progress text and the result.
- Tests: `OnboardingTests.cs` with fake `UpdateSteps` (no hardware): stock → reboot → drive → copy → VER ok; drive never appears → message "hold BOOT"; wrong-model UF2 refused before copying; VER reports the wrong model → failure; the RP2350 INFO_UF2 parser uses the real file text captured from the board.

- [ ] **Step 1:** Capture the RP2350's real `INFO_UF2.TXT` (`picotool reboot -u -f`, read the drive, then `picotool reboot -f`) into a test fixture.
- [ ] **Step 2:** Failing tests, then implement Core, then `dotnet test` green.
- [ ] **Step 3:** UI card; `--smoke` still passes. Add a `--smoke` screenshot of the new-board card driven by a fake state.
- [ ] **Step 4: Commit** "App: find boards without Touch Deck and install the right firmware".

### Task 7: Feed, bundle and CI for both models

**Files:**
- Modify: `.github/workflows/release.yml`: the firmware job also configures and builds `build-rp2350` and uploads `dist/rp2350-128.uf2`.
- Modify: `tools/publish_release.py` so a firmware release publishes both boards' UF2s.
- Modify: `tools/tests/test_make_updates.py`: two boards merge, each keeps its newest, `rp2350-128` is accepted by the `BOARD` regex.
- Modify: `windows-app/build.ps1` (Task 6 did the copy; here add a check that both firmwares are present for a release build).

- [ ] **Step 1:** Failing pytest for the two-board feed, then implement, then pass.
- [ ] **Step 2:** Dispatch a dry run on the branch (`gh_actions.py dispatch feature/rp2350-round`). Expected: green. The artifact has both UF2s, and the MSI contains both. Flash the CI `deck128.uf2` to the RP2350 and run `test_board_rp2350.py`: pass.
- [ ] **Step 3: Commit** "Release: build and publish rp2350-128 alongside rp2040-169".

### Task 8: Docs

**Files:** `CLAUDE.md` (a new RP2350 section: build dirs, pins, the RST 13 note, round layout copies, `TD_BOARD`), `windows-app/CLAUDE.md` (onboarding, UF2 inspection), `README.md` (supported boards, installing on a new board), and `THIRD-PARTY-NOTICES.txt` (Waveshare GC9A01 init).

- [ ] **Step 1:** Write them, then commit "Docs: RP2350 round board and onboarding".

### Task 9: Acceptance: a factory board installed by the app

- [ ] **Step 1:** Restore the factory demo: `picotool load -x %USERPROFILE%\.touchdeck\backups\rp2350-touch-lcd-1.28-factory.uf2 -f`. Expected: `2E8A:0009` on a COM port, printing IMU lines.
- [ ] **Step 2:** Build the MSI with `build.ps1 -Smoke` and run the built app (`out\publish\TouchDeck.exe`). The installed app stays untouched until release.
- [ ] **Step 3:** The app shows "New board found: RP2350". Choose "Touch LCD 1.28 (round)" and install.
  - **Expected:** the board reboots by itself (no BOOT press), the UF2 is copied, and the app connects to `rp2350-128` and shows the round mirror.
  - Record it with screenshots via `--smoke` or the owner's eyes.
- [ ] **Step 4:** Repeat from the bootloader: hold BOOT, or run `picotool reboot -u -f`. The app shows "RP2350 in bootloader" and installs the same way.
- [ ] **Step 5:** Wrong-model guard: with a 1.69 UF2 forced for the RP2350 through a test hook, the app refuses before copying. This is covered by a unit test, so the hardware run is optional.
- [ ] **Step 6:** Full suites: pytest, `dotnet test`, both firmware builds, ESP32 build, the RP2040 and RP2350 board tests. All green.
- [ ] **Step 7:** Final whole-branch review by a fresh reviewer, fix pass, then the PR after #9 merges.
