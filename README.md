# Touch Deck

A small touch-screen board that sits on your desk and works as a **clipboard that types**, a **mouse jiggler** and, on the RP2040 board, an **analog watch**. It comes with a Windows companion app.

- **Copy:** tap COPY on the board and it grabs the text you've selected on the PC.
- **Paste:** tap PASTE and the board types that text back as real keystrokes. That works anywhere, including in places where Ctrl+V is blocked, such as remote consoles, VMs and login prompts.
- **Clip memory:** the clip lives in the board's own memory, never on the Windows clipboard.

![Touch Deck for Windows](windows-app/docs/img/main-window.png)

## What's in this repo

| Folder | What | Stack |
|---|---|---|
| [`src/`](src) | Firmware for the **Waveshare RP2040-Touch-LCD-1.69** and **RP2350-Touch-LCD-1.28** (round) | C, Pico SDK 2.1.1, TinyUSB, dual core |
| [`esp32c3/`](esp32c3) | Firmware for the **ESP32-2424S012C** (1.28" round) | Arduino-ESP32 on PlatformIO, LovyanGFX, NimBLE |
| [`windows-app/`](windows-app) | **Touch Deck for Windows**: companion app and MSI installer | C# .NET 8 WPF, WiX v5 |
| [`tools/`](tools) | Flasher, font and letter generators, release publisher, perf script, tests | Python 3, pyserial, Pillow, pytest |
| [`.github/workflows/`](.github/workflows) | Release pipeline: a tag builds firmware and the MSI and publishes them | GitHub Actions |
| [`docs/`](docs) | Design specs and implementation plans | Markdown |

All the boards speak the same serial line protocol, which is documented at the top of [`src/usb_io.c`](src/usb_io.c). Any PC-side tool (the Windows app, and a future macOS app) talks to either board the same way.

## The boards

### RP2040-Touch-LCD-1.69 (240x280, touch, buzzer)

It shows up on the PC as a USB composite device (`CAFE:4011`): a keyboard, a mouse and a serial port. Swipe between three pages:

- **Watch.** A rounded-square analog face that ticks every second on the speaker. The tick is driven by a hardware timer, so it's exactly on the second. There's a mute toggle in the corner; it silences only the tick, and touch clicks always sound. The **BOOT button** starts and pauses a stopwatch; a long press resets it.
- **Clipboard.** COPY asks the PC for the selected text; PASTE types it as US-layout keystrokes, adjusting for Caps Lock.
- **Clipboard trash.** A bin next to the title clears the board's clip. It only works when there's text and no paste is typing.
- **Jiggler.** The dot drives along an outlined letter lane (O W M N Z X C V H J L B G D) and the mouse follows it in proportion, drifting within the lane like a car in its lane. O, B and D loop; the other letters bounce back at their ends. Every 45-150 s it runs its **menu event**: it stops, right-clicks and holds the context menu open, presses Esc (or F15, a key no app acts on), pauses, then glides on to a new random letter. The cog at the Jiggler page's lower left opens the **Jiggler menu** panel. There you set context menu on/off, Esc or F15, how long the menu stays open (default 2 s) and the pause before the next letter (default 0 s), each 0-60 s. Close it with the X. The settings are saved on the board, and the app's Settings dialog edits them too. The mouse always stays in the area where it started. The scale pill (top-left) and BOOT change the size (1x, 1.5x, 2x). The ON/OFF pill (top-right) and a tap on the letter turn it on and off.

Mute, jiggler on/off and size are saved to flash, and they survive power-off and firmware updates. The core runs at 200 MHz. Only the parts of the screen that change are redrawn, and each watch second is drawn in advance so it appears about 8 ms after the tick.

### ESP32-2424S012C (1.28" round, touch)

The ESP32-C3 has no USB keyboard/mouse hardware, so it has two **output modes**:

- **BT:** a Bluetooth LE keyboard and mouse, paired explicitly with an on-screen 6-digit passkey.
- **PC:** the board sends its key and mouse reports over USB serial, and the PC app performs them.

It has Clipboard, Jiggler and Settings pages with the same trash can, letter lanes and Jiggler menu panel. **F15 over Bluetooth:** firmware 1.8.0 widened the board's keyboard description so it can send F15. If F15 doesn't arrive after updating, Windows is using the copy it saved when you paired, so remove the board in Windows Bluetooth settings and pair it again. On the round screen the jiggler's pills sit either side of the letter. There's no clock or speaker on this board.

### RP2350-Touch-LCD-1.28 (240x240 round, touch)

The round screen on an RP2350 (Pico 2 chip). It is a real USB keyboard and mouse like the 1.69 (`CAFE:4011`, the same firmware tree), with the ESP32-C3's round **Clipboard** and **Jiggler** pages and a `USB` chip. There's no watch, speaker or Bluetooth. The BOOT button cycles the jiggler size, like the scale pill. The RP2350 has a floating-point unit, so it draws about ten times faster than the RP2040: a full jiggler page takes about 10 ms instead of about 120 ms.

A new board still runs Waveshare's factory demo. Plug it in with the Windows app open and it offers **Install Touch Deck** (see below); no buttons needed.

## Touch Deck for Windows

Install `TouchDeck-<version>.msi`. It installs just for you, needs no admin rights, and doesn't need .NET installed. The app:

- shows the **device itself**: a live copy of the board's screen in a board-shaped frame (firmware 1.7.0 and later). Click it to tap, drag sideways to swipe. It is drawn by the firmware's own page code, compiled for the PC, so it matches the board pixel for pixel;
- finds the board and shows its **firmware version**;
- handles COPY by reading the selected text (or, if nothing is selected, the clipboard);
- performs keys and mouse input in PC mode;
- updates the firmware of RP boards (the 1.69 and the round RP2350) with one click, from firmware bundled in the app;
- finds a **new board** (a Raspberry Pi board on its factory firmware, or in its bootloader) and offers **Install Touch Deck** with the right firmware for that model. It checks that the file is for that chip and board before touching the board;
- sends the current selection to the board with **Ctrl+Alt+C** from any app;
- keeps a list of recent clips in memory only;
- gives you remote page and button controls, live board diagnostics, and dry-run mode;
- with firmware 1.6.x, shows a live **jiggler card** instead of the device (letter, moving dot, ON/OFF and size), plus the board's clip length with a Clear button;
- lives in the tray.

See [`windows-app/README.md`](windows-app/README.md) for details and build steps.

## Build

**RP firmware (RP2040 1.69 and RP2350 1.28).** The toolchain comes from the VS Code Pico extension in `~/.pico-sdk`. One source tree, one build directory per board:

```bash
P=~/.pico-sdk; export PATH="$P/ninja/v1.12.1:$P/cmake/v3.31.5/bin:$PATH"
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release                                   # once
ninja -C build                                                                           # -> build/watch.uf2 (1.69)
cmake -S . -B build-rp2350 -G Ninja -DCMAKE_BUILD_TYPE=Release -DTD_BOARD=rp2350_128      # once
ninja -C build-rp2350                                                                    # -> build-rp2350/deck128.uf2 (round)
python tools/flash.py [--board rp2350-128]              # BOOT over serial, copy to the UF2 drive
```

Both RP boards enumerate as `CAFE:4011`, so the tools pick a board by the model its `VER` reports (`rp2040-169` or `rp2350-128`).

You can also flash from the Windows app with **Install firmware**.

**ESP32-C3 firmware:**

```bash
cd esp32c3
python -m platformio run                                  # build
python -m platformio run -t upload --upload-port COM7     # flash (no BOOT button needed)
```

**Windows app:**

```powershell
cd windows-app
.\build.ps1          # bundle firmware from ../build, test, publish, MSI -> out\TouchDeck-<ver>.msi
```

The app build also compiles the device renderers (`hostui/build.bat`, needs Visual Studio's C++ tools): the firmware's page code as `tdui_rp2040.dll`, `tdui_esp32c3.dll` and `tdui_rp2350.dll`. `hostui/build.sh` builds the same libraries for macOS or Linux.

Only one program can hold the board's serial port at a time. Quit the app (tray → Quit) before using `flash.py` or the board tests.

## Releases and updates

The app updates itself. **Settings** (the cog in the header) shows the app version and the connected board's firmware. It checks for updates at start and once a day, or when you press **Check for updates now**. App and firmware updates are offered and installed separately.

To ship a release:

```bash
# bump <Version> in windows-app/Directory.Build.props (and/or FW_VERSION in src/version.h), commit, then:
git tag app-v1.3.0 && git push origin app-v1.3.0     # app (+ its bundled RP2040 firmware)
git tag fw-v1.7.0  && git push origin fw-v1.7.0      # firmware only
```

- **Build and publish:** GitHub Actions (`.github/workflows/release.yml`) builds and tests everything. It then publishes the MSI, the UF2 and a complete `updates.json` as a release in the public repo [SynexyConsulting/TouchDeckUpdates](https://github.com/SynexyConsulting/TouchDeckUpdates).
- **Signed feed:**
  - Every `updates.json` is signed (`updates.json.sig`, ECDSA P-256).
  - The app has the public key built in and refuses any feed whose signature doesn't verify, so a leaked publish token alone can't ship an update.
  - The private key is `%USERPROFILE%\.touchdeck\feed-signing-key.pem`. It is **never** committed. **Back it up**: losing it means shipping a new app build with a new key.
- **CI setup (done):** the GitHub Environment `release` releases its secrets only to runs for `app-v*` / `fw-v*` tags, under Deployment branches and tags. A tag ruleset ("release tags") restricts who may create those tags. The secrets:
  - `TOUCHDECK_UPDATES_TOKEN`: a fine-grained token with *Contents: read and write* on TouchDeckUpdates only, expiring after 1 year, so renew it then;
  - `TOUCHDECK_FEED_KEY`: the PEM file's text.
  Required reviewers would need GitHub Enterprise for a private repo.
- **By hand:** `python tools/publish_release.py --key-file %USERPROFILE%\.touchdeck\feed-signing-key.pem ...` does the same publish from your PC (see `--help`). It refuses a key that doesn't match the app's.
- **What the app trusts:**
  - only a correctly signed feed;
  - only `https` downloads from that repo's releases (and GitHub's download hosts);
  - each download checked against the SHA-256 and size in the feed, with read timeouts, before anything runs.

## Tests

- `pytest tools/tests` covers:
  - the release feed builder (`make_updates.py`);
  - the font generator, including a check that every UI label fits its button;
  - the jiggler letters: every lane fits both screens, the dot stays inside its lane, letters loop or bounce correctly, and the mouse stays bounded. This drives the shared C engine, built on the PC with MSVC;
  - `gfx_line`, checked pixel for pixel against the reference algorithm, also built with MSVC;
  - the device renderers (`hostui/`): every page of both boards, the sync lines round-tripping through the parser, and (`test_board_mirror.py`, with the RP2040 connected) the host render of what the board streams equals the board's own framebuffer (`FBCRC`).

  The MSVC-based tests skip without Visual Studio. The `test_board_*` files drive a connected board and skip when it's absent.
- `dotnet test windows-app/TouchDeck.sln` covers the app: protocol, injection, session, device manager, settings, firmware update and the device mirror, plus hardware tests (one checks the mirror against the board's framebuffer).
- `TouchDeck.exe --smoke DIR --smoke-steps` then `python tools/mirror_check.py DIR` drives the board through the app and compares the app's picture with the board.
- `python tools/perf_rp2040.py [--watch]` reports the RP2040's frame times (full redraws and animation).
- `windows-app/tools/install-smoke.ps1` is a real install, run and uninstall check of the MSI.

## Firmware versions

`src/version.h` and `esp32c3/src/version.h` define the board ID and version (1.7.0), and the `VER` command reports them. Bump `FW_VERSION` whenever the firmware changes. The Windows app compares the board's version with the one it bundles and offers the update.

## Credits

- Fonts: [Barlow](https://github.com/jpt/barlow) and [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) under the SIL Open Font License, and [DSEG7](https://github.com/keshikan/DSEG) for the stopwatch. Their license texts are in [`tools/fonts/`](tools/fonts).
- Hardware: Waveshare RP2040-Touch-LCD-1.69, Waveshare RP2350-Touch-LCD-1.28 and ESP32-2424S012C.
- This project has no license file yet.
