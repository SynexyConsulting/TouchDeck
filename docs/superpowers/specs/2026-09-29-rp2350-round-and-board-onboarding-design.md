# RP2350 round board and new-board onboarding: design

Status: draft for review, 2026-09-29.

## Goal

1. Touch Deck runs on the **Waveshare RP2350-Touch-LCD-1.28**, a round 240×240 touch board with an RP2350. It works as a native USB keyboard and mouse, like the RP2040 1.69.
2. The Windows app **notices boards that don't run Touch Deck yet** and installs the right firmware on them:
   - a new board still on its factory demo;
   - a board sitting in its bootloader.
3. The update feed carries **one firmware per board model**, and the app never flashes a model's firmware onto a different board.

Out of scope here: flashing ESP32 boards from the app (its own spec, phase 3 below), the macOS app, and "Touch Deck HID" naming (open question to the owner).

## Depends on

- **PR #8:** CI builds firmware with Arm GNU 14.2.Rel1.
- **PR #9, device mirror / firmware 1.7.0:** it splits page drawing into `ui_pages.c`, drawn from a plain `ui_state_t`. The round pages come from that split and the ESP32's round layout. Build on #9 after it merges; don't fork a second copy of the pages.

## The board (checked against Waveshare's demo source and the board itself)

| | RP2350-Touch-LCD-1.28 | Source |
|---|---|---|
| MCU | RP2350 A2, dual Cortex-M33 with FPU, 520 KB SRAM, 16 MB flash | `picotool info` on the board |
| LCD | GC9A01A 240×240 round; SPI1 DC 8, CS 9, CLK 10, MOSI 11, **RST 13**, BL 25 | demo `DEV_Config.h` (the SDK's board header wrongly says RST 12, which is MISO) |
| Touch | CST816S on I2C1 SDA 6 / SCL 7, **INT 21, RST 22** | demo `DEV_Config.h` |
| IMU | QMI8658 on the same I2C1, INT1 23 / INT2 24 (unused) | CircuitPython board pins |
| Battery | ADC on GPIO 29 (1/2 divider) | demo; it read 3.2 V on USB |
| Buzzer, power latch | none | schematic |
| Factory firmware | USB `2E8A:0009` (SDK stdio + reset interface), prints IMU and battery readings | on the board; backed up to `%USERPROFILE%\.touchdeck\backups\rp2350-touch-lcd-1.28-factory.uf2` |

## Firmware

**One CMake project, one source tree, two targets:**
- `watch`: `rp2040-169`, the existing board, unchanged.
- `deck128`: `rp2350-128`, built with `PICO_PLATFORM=rp2350-arm-s` and `PICO_BOARD=waveshare_rp2350_touch_lcd_1.28`.
- Pico SDK 2.1.1 supports both. CMake builds each target in its own build directory (`build/` and `build-rp2350/`), because the SDK sets its platform once per configure.

**Hardware code is split per board behind small interfaces:**
- `board_*.h`: pin map.
- `lcd_st7789.c` / `lcd_gc9a01.c`: same API (`lcd_init`, `lcd_push_frame`, `lcd_push_rect`, `lcd_wait`).
- The CST816 touch driver is shared as is: interrupt-gated and never read on a timer. The 1.28's CST816S behaves like the 1.69's CST816T for our reads.

**Shared as is:** USB (HID and CDC protocol), typer, jiggler engine (`jig_motion`, `jig_lane`, `jig_paths`), settings, gfx, fonts, mirror sync.

**UI:** the round layout from the ESP32 port (the same 240 px circle, pill buttons, edge ring, trash can at (178,42)). Its page drawing moves into the shared `ui_pages.c` from PR #9, so the ESP32 and the RP2350 draw the same round pages.
- Pages: **Clipboard** and **Jiggler**.
- No watch face, because there's no buzzer to tick and the round watch face isn't designed yet. Easy to add later.
- No Bluetooth settings page, because this board is USB HID.
- The chip shows `USB` in amber, as on the 1.69.

**Performance:**
- The RP2350 has a single-precision FPU, and the drawing is float math, so screens should draw several times faster than on the RP2040. Measure with `perf_rp2040.py` (renamed `perf_board.py`) rather than assume.
- The framebuffer is 115 KB (240×240×2), so a second buffer fits in 520 KB. Draw the next frame while the last one is being pushed only if the measurements show it's needed.

**Settings:** last flash sector, same record format. `PICO_FLASH_SIZE_BYTES` comes from the board header (16 MB).

**Identity:**
- `FW_BOARD "rp2350-128"`, answered by `VER`.
- USB stays `CAFE:4011`, because the app identifies the model by `VER`, not by USB ID.
- The model string is also embedded as Pico binary info (`bi_decl(bi_program_description(...))`) and as a fixed marker the app can find in the UF2 before flashing (see Safety).

## App: onboarding new boards

**What the app recognises today:** only `CAFE:4011` (our RP2040 firmware) and `303A:1001` (ESP32-C3).

**What it will recognise:**

| USB | What it is | What the app offers |
|---|---|---|
| `CAFE:4011` | Touch Deck firmware, any RP model (asks `VER`) | as today, with updates per model |
| `2E8A:000A` / `2E8A:0009` | RP2040 / RP2350 running a stock Pico SDK program (like the factory demo) | "New board found: install Touch Deck" |
| `2E8A:0003` / `2E8A:000F` | RP2040 / RP2350 in its bootloader (drive `RPI-RP2` / `RP2350`) | the same, flashing straight away |
| `303A:1001` answering `VER` | Touch Deck on an ESP32-C3 | as today |
| `303A:1001` silent | ESP32-C3 in download mode or foreign firmware | phase 3: install; until then, explain how to flash it |

**Install flow:**
1. **Pick the model.** The chip family is known from the USB ID, but not the screen, so the app shows the models built for that chip. With only one choice, it's preselected:
   - RP2040: `rp2040-169` (1.69" rectangle);
   - RP2350: `rp2350-128` (1.28" round).
2. **Reboot a running stock program into the bootloader** by opening its serial port at **1200 baud**. The SDK's stdio USB enables this by default, and the factory demo uses stdio USB.
   - Fall back to the SDK reset interface (vendor interface, WinUSB) only if the demo turns out to have disabled 1200-baud reset. It enumerated a "Reset" interface here, so it's there if needed.
   - If neither works, ask the user to hold BOOT while plugging in.
3. **Copy the UF2** to the bootloader drive, wait for `CAFE:4011`, and confirm `VER` reports the chosen model.

The firmware comes from the same bundle and feed the app already uses, now keyed by model.

## Safety: never the wrong firmware

`Uf2` validation today accepts only the RP2040 family ID. It changes to:
- **Chip check:** the UF2's family ID must match the chip the bootloader belongs to: RP2040 `0xE48BFF56`; RP2350 ARM-S `0xE48BFF59`, plus the RP2350 "absolute" block `0xE48BFF57` that SDK 2.x emits first.
- **Model check:** the UF2 must contain the marker `TDBOARD:<model>;`, and it must equal the model being installed. That's the chosen model for a new board, or the model `VER` reported for an update.

A 1.69 build never lands on a round board, or the reverse. Both checks are unit-tested with real built UF2s.

## Feed and bundle

- `make_updates.py` already keeps the newest firmware per `board`. Add `rp2350-128`: the id matches the existing `BOARD` regex, and a test pins it.
- The app bundles every model's UF2 under `firmware/`, with the manifest listing each `board`.
- CI builds both targets and publishes both.
- **Release order:** PR #8, then PR #9, then this, released as firmware 1.8.0 / app 1.4.0 or whatever the versions are then.

## Tests

- **Host:** UF2 chip and model checks (right, wrong model, wrong chip), onboarding USB-ID mapping, model picker choices, feed builder with two boards.
- **Board:** a new `test_board_rp2350.py` mirrors the RP2040 board tests (swipe, tap, jiggler on/off/scale, clip clear, `STATE`), skipping when absent. Perf numbers are recorded in the PR.
- **Mirror:** `FBCRC` host-vs-board comparison on every page, as PR #9 does for the RP2040.
- **End to end:** starting from the factory firmware, the app detects the board, installs, and it comes back as `rp2350-128`. Restore the factory UF2 to repeat.

## Phases

1. **Firmware for `rp2350-128`:** display, touch, the two pages, USB HID, board tests.
2. **App onboarding** plus model-aware UF2 checks, feed and bundle for both RP models, CI for both.
3. **ESP32 flashing from the app:** its own spec. It's the ROM loader protocol over the USB-Serial-JTAG port, with no esptool dependency.

## Open questions for the owner

- A round watch face for this board: later, or now?
- "Touch Deck HID": what the name should mean (still open from before).
