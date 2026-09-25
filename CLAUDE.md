# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Firmware (Pico SDK, C) for the **Waveshare RP2040-Touch-LCD-1.69** ("Touch Deck"): three swipeable screens — analog watch with buzzer tick, a device-memory clipboard that *types* its text over USB HID, and a mouse jiggler — plus Python tools in `tools/`.

A second port lives in `esp32c3/` for the **ESP32-2424S012C** (see "ESP32-C3 port" below).

## Build & flash

Toolchain comes from the VS Code Pico extension in `~/.pico-sdk` (SDK 2.1.1, GCC 14.2, picotool 2.1.1); `CMakeLists.txt` points at it directly, so no `PICO_SDK_PATH` env var is needed. cmake/ninja are not on PATH by default:

```bash
P=~/.pico-sdk; export PATH="$P/ninja/v1.12.1:$P/cmake/v3.31.5/bin:$PATH"
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release   # once
ninja -C build                                            # -> build/watch.uf2
```

Flash: `python tools/flash.py` — sends `BOOT` over the firmware's serial port, waits for the UF2 drive (**J:** here, `INFO_UF2.TXT` says `RPI-RP2`), copies `build/watch.uf2`. No BOOT button needed. It fails if `clip_helper.py` is holding the port — stop the helper first. The firmware enumerates as `CAFE:4011` (composite: CDC + HID keyboard/mouse), not the SDK's `2E8A:000A`, so picotool's reset interface is *not* available; opening the port at 1200 baud also reboots to the bootloader.

PC helper: `python tools/clip_helper.py` (needs `pyserial`, `uiautomation`). `--send TEXT` pushes a clip, `--boot` enters the bootloader.

There are no tests; verification is visual/audible on the device. The serial protocol can be exercised from Python without typing on the PC (HELLO→PONG, TIME, CLIP); PASTE and the jiggler inject real keystrokes/mouse input, so only trigger them deliberately.

## Hardware facts (from the schematic, not the demo code)

Pin map lives in `src/board.h`. Non-obvious points:
- LCD is ST7789V2 240x280 on SPI1; the visible area starts at **controller row 20** (`Y_OFFSET` in `lcd.c`). Init sequence is copied from Waveshare's `LCD_1in69.c`.
- Buzzer/speaker is on **GPIO2**, AC-coupled through a 10µF cap into an SS8050 transistor. A DC level is silent; sound needs a PWM square wave. Waveshare's demo code never uses it.
- **GPIO15 (SYS_EN)** latches board power on battery; `main()` drives it high first thing.
- Touch (CST816T @0x15, chip ID 0xB5), IMU (QMI8658) and RTC (PCF85063) share I2C1 on GPIO6/7. Only touch is used. Because touch reads are INT-gated, any future IMU/RTC code on the same bus must not assume the touch chip is idle-safe to address. Time comes from `__TIME__` until the PC helper sends `TIME`.

## Architecture

**Two cores.** core0 (`main.c`) runs USB (`tud_task` via `usb_io_poll`), touch polling, and the typing/jiggler state machines — all non-blocking, time-stepped. core1 (`ui.c`) only renders and pushes frames, because a full redraw takes tens of ms and would otherwise stall HID reports. They share `app_t app` (`app.h`); `clip`/`msg` are guarded by `clip_mtx`, core0 requests redraws by bumping `app.redraw_seq`, core1 signals `tick_pending` back so the buzzer (core0-only) ticks when the new second is actually on screen.

- `usb_io.c` + `usb_descriptors.c`: one HID interface with report IDs (1 keyboard, 2 mouse) plus CDC. The CDC line protocol with `tools/clip_helper.py` is documented at the top of `usb_io.c`. HID is device→host only; the host's single message back is the keyboard LED state, used to compensate for Caps Lock while typing.
- `typer.c`: types `app.clip` as US-layout keystrokes (TinyUSB `HID_ASCII_TO_KEYCODE`), press/release with small gaps. Non-ASCII is skipped on the board; the helper transliterates before sending.
- `jiggler.c`: relative mouse deltas that track a wobbling circle (accumulated rounding, so no drift from truncation), with a periodic STOP → right-click → wait → ESC → resume sequence. A paste waits until the jiggler is back in `JIG_CIRCLE`, then pauses it.
- `touch.c`: classifies tap / long-press / swipe itself instead of using the chip's gesture engine. **Never read the CST816 on a timer.** Unsolicited reads wedge it within seconds, holding SDA low. Reads happen only after a TP_INT falling-edge IRQ, and finger-lift is inferred from INT pulses stopping (`RELEASE_MS`). A failed read triggers an I2C bus clear plus a chip reset. The `DBG` serial command reports touch stats without touching the chip; `python tools/clip_helper.py --debug` logs it every 2 s.
- `lcd.c`: ST7789 driver. Commands go over 8-bit SPI; `lcd_push_frame()` switches SPI to 16-bit frames and DMAs the whole framebuffer (16-bit frames give the panel's big-endian byte order for free). Always `lcd_wait()` before writing to `fb` again: it restores 8-bit mode and releases CS.
- `gfx.c`: one full-screen RGB565 framebuffer (`fb`, ~134 KB of 264 KB RAM, so double buffering won't fit). Primitives are signed-distance based and anti-aliased by blending into existing pixels, so draw order matters. Discs/rings fill solid spans and only do distance math on edge pixels. Text uses Waveshare/ST 1-bit fonts in `src/fonts/`.
- `settings.c`: persistent settings (`muted`, `jig_on`) in the last 4 KB flash sector (`PICO_FLASH_SIZE_BYTES - 4K`; the board has 16 MB but the `pico` board config assumes 2 MB, which is fine). Append-only 256-byte records, newest valid wins, erase only when all 16 pages are used. Writes go through `flash_safe_execute`, which requires core1 to have called `flash_safe_execute_core_init()` (first line of `ui_core1_main`) — keep that if core1's entry changes. UF2 flashing doesn't touch that sector, so settings survive firmware updates. Add new fields to `record_t` by bumping `MAGIC` and keeping a reader for the previous layout (see `MAGIC_V1`) so users don't lose settings on upgrade. A saved `jig_on` restarts the jiggler at boot via `jiggler_set(true)`; it only moves once USB is mounted.
- `ui.h` holds button/hit-area geometry shared by drawing (core1) and touch handling (core0).

## ESP32-C3 port (`esp32c3/`)

This is a PlatformIO project (Arduino-ESP32 2.0.17, LovyanGFX, NimBLE-Arduino 1.4) for the ESP32-2424S012C: ESP32-C3, 1.28" round GC9A01 240x240 screen, CST816D touch (chip ID 0xB6). It has the clipboard and jiggler screens only. The board has no speaker and no real-time clock, and the time shown comes from the helper's `TIME`.
- Build: `cd esp32c3 && python -m platformio run`. Flash: `python -m platformio run -t upload --upload-port COM7`. No BOOT button needed; esptool resets the chip over USB. Stop `clip_helper.py` first, because it holds the port.
- **HID goes over Bluetooth LE, not USB.** The C3 has no USB device controller, only a fixed USB-Serial-JTAG at `303A:1001`. The USB cable carries only the helper protocol. `ble_ready()` means connected and subscribed; typing and jiggling wait for it, and the jiggler shows "Waiting for BT" meanwhile.
- **Pairing is explicit** (Settings page, cog icon → Bluetooth sub-page). `ble_hid.cpp` keeps a state machine: `BT_UNPAIRED` (not advertising at all), `BT_PAIRING` (advertising for 120 s with a random 6-digit passkey on screen), `BT_WAITING` (bonded, advertising for that PC), `BT_CONNECTED`, and `BT_OFF` (the user tapped Disconnect, bond kept). It uses DisplayOnly + MITM, and outside pairing mode `onPassKeyRequest` returns an unseen random code, so strangers can't pair. It keeps one bond: Pair and Forget delete all bonds. After Forget, the PC must also remove the device on its side. The PC's name is read from its GAP Device Name (0x2A00) after pairing and stored in `Preferences` key `host`. `python -m pip install bleak` gives a BLE scanner for checking whether the board is advertising.
- **Opening the C3's port must keep DTR and RTS low.** Those lines are its reset and boot-mode lines. `tools/touchdeck.open_serial()` handles this: DTR high for the RP2040, whose TinyUSB only transmits when DTR is set, and low for the ESP32.
- `CORE_DEBUG_LEVEL=0` is required, because Arduino core log lines on `Serial` corrupt the helper protocol.
- Code layout mirrors the RP2040 firmware. `gfx.c` and `fonts/` are copies (the framebuffer is heap-allocated in DMA-capable RAM), `touch.cpp` is the same interrupt-gated CST816 driver, and `typer`/`jiggler` are the same state machines with slower Bluetooth pacing (12 ms key hold/gap, 15 ms mouse step). The Arduino `loop()` runs at priority 2 for logic; `ui_task` renders at priority 1, because the C3 has one core and no FPU. The layout has to stay inside the 240 px circle.
- Settings: `Preferences` namespace `touchdeck`, key `jig` (jiggler on/off, restored at boot).
