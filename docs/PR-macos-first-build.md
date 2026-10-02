## macOS app: first build on a Mac, COPY from the selection, closer parity with Windows

The Mac app (written blind on Windows) now builds on Xcode 27, runs against a board, and passes its tests. The host C tests also run on macOS now.

### Bugs fixed
- **Updates were refused every time.** The feed check used `is Bool`, which is also true for NSNumber 0 and 1, so `{"schema": 1}` failed. It now checks for CFBoolean, and the live signed feed verifies and parses (`--smoke-check-updates`).
- **The renderer tests didn't build their libraries.** The test pre-action inherited Xcode's per-platform deployment targets, so clang aimed at visionOS. It now runs `hostui/build.sh` under `env -i`. `$(SRCROOT)` in the test environment needed a macro-expansion target.
- **COPY read the wrong app.** It now reads the app you were using: the frontmost app, or the last one when Touch Deck is in front. It walks up to 4 parent elements, as Windows does, and asks Chromium/Electron apps for their accessibility tree. It also reads selected ranges and WebKit text markers. When Accessibility can't tell, it sends ⌘C and restores your clipboard, including a copy that lands late. It doesn't send ⌘C when Accessibility says nothing is selected.
- **UF2 copy** is forced to the drive (`F_FULLFSYNC`).

### Parity with Windows
- **Main window:**
  - laid out like the Windows one: header with Settings; device card with a bezel, 1.25×, the hint and placeholder text;
  - details with Port, Firmware and Built; Install firmware and Bootloader, both confirmed first;
  - Remote with the Page buttons and BOOT; the send-info line; board-clip Clear;
  - recent clips with Resend, Copy and Clear; diagnostics as chips; Copy log.
- **Mirror:**
  - the first click on an inactive window is a tap;
  - only a live frame takes clicks;
  - round boards are true circles.
- **Updates:**
  - the hourly tick checks once a day, and a failed check counts (no 15 s loop);
  - the Windows texts and log lines;
  - download progress; firmware downloads check that the board matches;
  - the app reopens after Installer closes.
- **App lifecycle:**
  - single instance; `--quit` frees the port for `flash.py` and the board tests; reopen shows the window;
  - notifications show while the app is active and open the window when clicked.
- **Look and menu:**
  - a three-state menu bar icon (connected, idle, bad); Dry Run in the menu; Barlow and JetBrains Mono bundled (OFL);
  - the login-item approval note; stale-session events dropped; settings save errors logged; errors.log capped at 256 KB.
- **`--smoke DIR [--smoke-steps] [--smoke-check-updates]`:** window, mirror and settings PNGs plus `smoke.txt`, as on Windows.
- **Release feed:** `make_updates.merge(app_macos=...)` and `publish_release.py --app-macos PKG --app-macos-version X.Y.Z` (the Mac app has its own version line, never backwards). Until now the feed only ever had `app.windows`.

### Host C tests on macOS/Linux
- `jig_host.build_shared` builds with `cc`/`$CC` off Windows, and MSVC on Windows as before. `tdui_host` runs `hostui/build.sh`.
- `hostui/build.sh` picks FP contraction per board:
  - `-ffp-contract=off` for the RP2040 and ESP32-C3 (no FPU, nothing fused);
  - clang's default for the RP2350, whose GCC firmware fuses with VFMA. That matched the board's FBCRC on 78 of 80 discriminating watch seconds; `off` matched 42.
- **Before this, the Mac's RP2040 mirror would have been wrong on about 1.5% of watch seconds.**

### Also on this branch
- **License:** GPL-3.0-or-later (`LICENSE`), with plain words and third-party licences in `docs/license.md` and a website page `docs/license.html`.
- **Pico toolchain on macOS:** `tools/flash.py` finds UF2 drives in `/Volumes`. Disabled pill buttons are dimmed, and the install offer is in view.
- **Housekeeping:** `.gitignore` now blocks signing material and keys; a made-up host name replaces a real one in a font test.

### Tests
- XCTest: 59 passed (was 51 passing at the start, after the compile fixes).
- pytest on the Mac: 146 passed, 35 skipped (absent RP2040 and ESP32 boards, no ARM compiler); `test_board_rp2040.py` deselected. Before: 92 passed, 106 skipped.
- Against the real RP2350 round board: `test_board_rp2350.py` 15/15, `test_board_mirror_rp2350.py` 5/5, and the app's smoke and smoke-steps runs (mirror, ANIM, a sent clip).

### Not verified, needs a person
- **Accessibility paths:** the debug build is ad-hoc signed and not yet trusted. Try COPY in Safari, Chrome, VS Code, Notes and Terminal, plus ⌃⌥C.
- **Firmware install from the app:** this Mac has no Pico toolchain, so no UF2s are bundled.
- **The real window:** pill buttons show thin end bars in the `cacheDisplay` snapshot, which is probably an artifact of the snapshot.
- **RP2040 mirror on macOS** (sinf/cosf vs the board): run `test_board_mirror.py` with that board attached.

### Follow-ups
- **The RP2350 mirror is off on about 1% of watch seconds** on Windows (MSVC doesn't fuse) and on Intel Macs. Building the RP2350 firmware with `-ffp-contract=off` would make every host match exactly.
- **Windows hides BOOT for the ESP32-C3**, but firmware 1.8.0 handles `BTN` there. The Mac shows it for every board.
- **A board plugged in after the first update check** isn't offered firmware for 24 h. This happens on both apps.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
