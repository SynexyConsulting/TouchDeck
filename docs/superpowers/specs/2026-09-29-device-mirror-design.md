# Device mirror in the app: design

Status: written for review on 2026-09-29 (built overnight on `feature/device-mirror`, draft PR).
Scope: RP2040 and ESP32-C3 firmware 1.7.0, the Windows app, and a portable C renderer that a macOS app can reuse.

## Goal

The app shows the device itself: a live, device-shaped copy of the board's screen that you can click and drag like the touch panel. That replaces the per-feature cards (jiggler card, board-clip row). The main window becomes the device mirror, a remote pane and the activity log. Device details and firmware install stay reachable, and Settings stays as it is.

Constraints from the owner:

- Streaming the real screen is allowed only if it works on the RP2040 and its successors and on the ESP32 families, **without dropping frame rate**.
- Otherwise render by code, driven by state synced from the device.
- It must also work in the planned macOS app.

## 1. Measurements (spike)

All measured on the RP2040-Touch-LCD-1.69 on COM6, firmware built from `main` at 1.6.0 (200 MHz, software float). `tools/perf_rp2040.py` was extended for this. It now forces single full redraws with a harmless `TIME` command and reads DBG's `drawmax` after each one, because the `draw=` average is dominated by the small partial frames.

### Baseline frame cost (firmware 1.6.0)

| Frame | Cost |
|---|---|
| Full redraw, watch | 229-235 ms draw + ~10 ms push |
| Full redraw, clipboard | 111 ms draw |
| Full redraw, jiggler | 245-287 ms draw (depends on the letter) |
| Jiggler animation (`ANIM 1`), partial frames | 19.8 fps (20 fps target) |
| Watch, per-second partial frame | ~1 fps, drawn ahead and pushed on the tick |

### Streaming spike

This was a temporary firmware patch, not merged; the patch is kept with the PR notes. It had two parts:

- `SPIKE BLAST n`: core0 writes n KB of the framebuffer to CDC as fast as it can.
- `SPIKE STREAM 1`: after core1 pushes a frame or dirty rect to the LCD, it hands the rect list to core0 and waits until core0 has written those pixels to CDC. It has to wait: `fb` is the only framebuffer. Double buffering doesn't fit (134 KB of 264 KB RAM), and drawing the next frame while the old one is still being sent would send torn pixels.

| Measurement | Result |
|---|---|
| CDC throughput, core0 doing nothing else | 688-727 KB/s (256 KB in 360 ms; 1 MB in 1.41-1.48 s) |
| One full frame (240x280x2 = 134,400 B) over CDC | ~190 ms minimum |
| Clipboard full redraw with streaming, request to last byte on the PC | 357 ms (vs ~121 ms draw+push without) |
| Jiggler full redraw with streaming | 508 ms |
| Jiggler animation with streaming | **19.07 fps** (vs 19.81 without) at 95 KB/s |
| Watch with streaming | 1.11 fps (unchanged; per-second rects are small) |

The ESP32-C3 board wasn't connected, so it could only be reasoned about. Its USB-Serial-JTAG is a full-speed, fixed-function CDC with 64-byte endpoints, and in practice it is slower than TinyUSB's CDC. The C3 has a single core, so every byte sent is CPU time taken from the render task (priority 1) and the logic task. A full 240x240 frame is 115 KB, which is at least ~160 ms of transfer even at the RP2040's rate. That would add directly to its frame time.

### Decision

**Render by code.** Streaming measurably drops the frame rate on the RP2040: the animation drops ~4%, and each full frame keeps core1 blocked for an extra ~240 ms, so page swipes are slower. On the single-core C3 it would be worse. The owner's rule was: if streaming costs any frame rate, render by code.

Render-by-code costs the device almost nothing. It only sends a few hundred bytes of state per change, and it gives a pixel-identical copy, because the app runs the firmware's own drawing code.

## 2. Architecture: one renderer, two hosts

```
            firmware (core1 / render task)                 app (Windows now, macOS later)
 app_t --ui_state_fill()--> ui_state_t --ui_draw_page()--> fb --> LCD
   |                                                     same C: ui_pages.c + gfx.c + fonts + icons
   +--ui_state_fill()--> ui_sync.c --STATE/TEXT/CLIPTEXT--> MirrorState --> ui_state_t --> tdui_render() --> bitmap
```

- **`ui_state.h`** (identical in both trees) is a plain struct. It holds everything a page needs to draw itself: page, clock, stopwatch, clip (the first 1 KB), transient message, jiggler (letter, dot, phase, countdown, menus, uptime), and on the C3 the output mode and Bluetooth state. It has only fixed-size fields: int32, float, uint32 and fixed char arrays, with no padding, so C, C# and ctypes can share the layout. The shared enums (`CLIP_*`, `JIG_*` phases, `JIG_SCALES`) move here from `app.h`.
- **`ui_pages.c`** (one per board, plain C, no SDK) holds the page drawing moved out of `ui.c` / `ui.cpp`. It draws only from a `const ui_state_t *`, never from `app` or the clock. The firmware's `ui.c` / `ui.cpp` keeps what is hardware: the frame loop, dirty-rect scheduling, the LCD push and `ui_state_fill()`, which snapshots `app` under the clip mutex.
  - Sampling once per frame keeps the old behaviour: the dot was already sampled once per frame.
  - Because the snapshot is taken under the mutex, the page no longer holds `clip_mtx` while it draws text.
- **`ui_sync.c`** (identical in both trees, plain C) serialises a `ui_state_t` into protocol lines. It is also compiled into the host library, so the tests check that the C serialiser and the app's parser agree.
- **`hostui/`** builds the renderers for the PC:
  - `tdui_rp2040` and `tdui_esp32c3`, from each board's own `ui_pages.c`, `gfx.c`, `icons.c`, `jig_lane.c`, `jig_paths.c`, `aa_fonts.c` plus `hostui/tdui.c`.
  - Exports: `tdui_width`, `tdui_height`, `tdui_state_size`, `tdui_letter_index`, `tdui_render(state, out565)` and `tdui_state_line` (tests).
  - `hostui/build.bat` builds both with MSVC. It finds `vcvars64.bat` through `vswhere`, which is also how the CI runner finds it. `hostui/build.sh` builds `.dylib`/`.so` with clang or gcc for macOS and Linux.

### macOS portability

The renderer is ISO C11 with no platform calls: no allocation, no threads, no I/O. `build.sh` builds it with `cc -O2 -shared -fPIC` (`-dynamiclib` on macOS). A macOS app loads the same exports; in Swift, with the header imported through a bridging header or a module map. It fills the same `ui_state_t` from the same lines, and turns the RGB565 buffer into a `CGImage` (16-bit, 5-6-5, little-endian). Nothing in the protocol or the renderer is Windows-specific. The renderer uses single-precision float. MSVC (x64), gcc (x64 Linux, via `build.sh`) and the RP2040 itself produce identical frames on every page, so an Apple-silicon build is expected to match too. That has not been checked on a Mac.

## 3. Protocol additions (firmware 1.7.0)

`WATCH 1` already starts `STATE` reports, and 1.7.0 extends them. Everything is additive: older apps parse `STATE` into a dictionary and ignore unknown keys and unknown lines. So firmware 1.7.0 keeps working with the installed app 1.2.x.

**`STATE`** keeps its eight fields (`jig letter scale phase x y clip paste`) and adds:

| key | field | notes |
|---|---|---|
| `page` | screen index | presence of `page` marks a full mirror (1.7.0+) |
| `sub` | Settings > Bluetooth sub-page | C3 |
| `t` | clock, seconds since midnight | RP2040 watch |
| `pc` | app/helper connected | |
| `link` | chip dot: RP2040 USB mounted, C3 `out_ready()` | |
| `mute` | watch tick muted | RP2040 |
| `timer` | stopwatch seconds | RP2040 |
| `cst` | clip state (idle, copying, pasting) | |
| `ppos` | paste position | progress bar |
| `paused` | jiggler paused for a paste | |
| `demo` | `ANIM 1` demo | |
| `next`, `up`, `menus` | jiggler countdown, uptime, menus opened | |
| `mode`, `bta`, `bts`, `btr`, `left`, `pk` | C3: Bluetooth mode, bond available, BT state, ready, pairing seconds left, pairing PIN | C3 |

**`TEXT <key> <value>`** is for string fields, which may contain spaces:

- `msg` is the transient status message ("" when none).
- `src` is the clip source.
- `host` and `down` are the C3's bonded PC name and its "not ready" reason.

**`CLIPTEXT <escaped>`** carries the first 1024 bytes of the clip. The escapes are `\\`, `\n`, `\r`, `\t`, and `\xHH` for other bytes outside 0x20-0x7E. It is sent after `WATCH 1` and whenever the clip changes. The box shows at most 8 rows, so 1 KB is always enough to draw it. The full clip length is `STATE clip=`.

**When the board sends:**

- The board checks its state every 10 ms: `ui_state_fill` without the clip, then a memcmp.
- It sends `STATE` at once when any field other than the dot changes, and at most every 50 ms while the dot moves (20 Hz, the device's own animation rate; it was 10 Hz).
- It sends `TEXT` when a string changes, and `CLIPTEXT` when the clip's change counter moves.
- The watch clock makes one `STATE` per second.

**`FBCRC x y w h`** (tests) replies `LOG fbcrc <8 hex>`. That is a CRC-32 (zlib) of the framebuffer region, as little-endian RGB565 bytes, row by row. It reads `fb` from core0, so it is only meaningful on a page that isn't animating. The mirror tests use it to prove the app's rendering is pixel-identical to the device's.

`FW_VERSION` goes to 1.7.0 in both `version.h` files.

## 4. App

### Core (`TouchDeck.Core/Mirror`)

- `UiState`: `[StructLayout(Sequential)]` twin of `ui_state_t`. A test pins its size against `tdui_state_size()`.
- `MirrorState` applies `StateReport` (now with all numeric fields), `TextField` and `ClipText` messages and produces a `UiState`.
- `NativeUi.Render(BoardKind, UiState)` returns an RGB565 buffer. It P/Invokes `tdui_rp2040.dll` / `tdui_esp32c3.dll`. If a library fails to load, the mirror counts as unsupported and the app falls back.
- `MirrorInput` maps a pointer press and release, in device pixels, to a command:
  - Horizontal travel of at least 36 px, clearly more horizontal than vertical, is a swipe. Dragging left is `SWIPE L`, which goes to the next page, like a finger.
  - Travel of at most 12 px is `TAP x y` at the press point.
  - Anything else is ignored.

### Session

`DeviceSession` gains `Tap(x, y)` and raises `TextReceived` and `ClipTextReceived`. `MirrorSupported` keeps its meaning (any `STATE`). `AppController.FullMirror` is true once a `STATE` with `page` arrives and the renderer is loaded.

### Build

`TouchDeck.Core.csproj` runs `hostui\build.bat` before build, incrementally (inputs: the C sources), into `obj\native\`. The two DLLs are copied to the output, so the tests, the published app and the MSI get them; the MSI harvests the publish folder. No change to `release.yml` is needed: its Windows job already runs on a runner with Visual Studio 2022, and `build.ps1` builds the app.

### Window layout

| Column | Contents |
|---|---|
| Left | The **device**: the mirror in a device-shaped bezel. RP2040: 240x280 panel with the glass's 44 px corner radius. C3: a 240 px circle. Shown at 1.25x. Under it, one caption line: "Click to tap, drag sideways to swipe". Below that, the **device details** card, unchanged: status, port, firmware, build, Install/Reinstall firmware, Bootloader. |
| Right | The **remote pane**: page back/forward, BOOT button and long press (RP2040 only; the C3 firmware has no `BTN`), the send-text box and board-clip row, and the recent clips. Then the **activity log**, which fills the rest. The diagnostics card appears when enabled, as today. |

**Fallbacks:**

- Firmware 1.6.x (STATE without `page`): the mirror area shows today's jiggler card, with a note that firmware 1.7.0 shows the whole device.
- Older firmware (no STATE at all), or the renderer failed to load: today's fallback text.
- No board: the empty bezel with "Connect a Touch Deck".

## 5. Input mapping

| On the mirror | Sent |
|---|---|
| click (≤ 12 px travel) | `TAP x y` in device pixels |
| drag left / right (≥ 36 px, mostly horizontal) | `SWIPE L` / `SWIPE R` |
| remote pane Button / Long press | `BTN` / `BTN LONG` |
| remote pane Page ◀ / ▶ | `SWIPE R` / `SWIPE L` |

Taps go to the firmware's own hit testing (`on_touch`). So the mirror can't drift from the device: the Copy, Paste, trash, mute, scale and on/off areas behave exactly as they do under a finger. That includes Paste, which types on the PC.

## 6. Testing

- **Host (pytest, MSVC):** `test_ui_host.py` builds both renderers through `hostui/build.bat` and checks:
  - the struct size;
  - that each page renders and differs from the others;
  - the jiggler dot drawn at the synced position;
  - that the RP2040 and C3 layouts stay inside their panels;
  - that the `ui_sync.c` lines round-trip through the Python parser.
- **Board (pytest, COM6):** `test_board_mirror.py` drives the real board with `SWIPE`, `CLIP` text, `JIG SCALE`, `TAP` and `ANIM 1`. It parses the board's `STATE`/`TEXT`/`CLIPTEXT` into the struct, renders on the host, and compares with `FBCRC` of the real framebuffer. Every page must match exactly, the watch included (its `sinf`/`cosf` agree between the Pico SDK and the PC). Right after each second, the watch's `fb` holds the drawn-ahead frame for t+1, so t or t+1 is accepted.
- **App (xUnit):**
  - parsing of the new lines and the escaping;
  - `MirrorState` to `UiState`;
  - the layout against the DLL, and a round trip through `tdui_state_line`;
  - `MirrorInput` thresholds;
  - session `Tap`;
  - a hardware test that opens the real board, drives it through `DeviceSession`, renders with `NativeUi` and compares with `FBCRC`.
- **Smoke:** `build.ps1 -Smoke` writes `smoke.png`, which now shows the mirror, and `mirror.png`, the raw 1x render. `--smoke-steps` drives the board through the app (pages, `ANIM 1`, a clip sent from the app). `tools/mirror_check.py` then compares the app's final frame with `FBCRC`.
- **Frame rate:** `perf_rp2040.py` before and after, recorded below.

## 7. Results

### 7.1 Pixel identity

| Check | Result |
|---|---|
| Host render (MSVC) vs the RP2040's framebuffer, via `FBCRC` | identical on the clipboard (with text, empty, with a message), the jiggler at all three scales, and the whole watch face |
| The app's own mirror (`--smoke-steps`, the real WPF path) vs `FBCRC` | identical (`mirror_check.py`) |
| gcc on Linux (`build.sh`) vs MSVC | identical CRCs on all six sample pages of both boards |

The mirror test found one real device bug. When the app connected or left, the RP2040 changed `app.helper` without asking for a redraw, so the watch kept a stale "PC" label until the hands happened to repaint that area. The fix is to redraw on that change, as the ESP32-C3 already did.

### 7.2 Frame rate before and after (RP2040, `perf_rp2040.py`, medians)

| | 1.6.0 (main) | 1.7.0 | 1.7.0 streaming the mirror (`--watch`) |
|---|---|---|---|
| Jiggler animation (`ANIM 1`) | 19.77 / 19.80 fps | 19.83 / 19.89 fps | 19.85 / 19.83 fps |
| Watch, per-second frames | 1.11 fps | 1.11 fps | 1.11 fps |
| Full redraw, clipboard | 110.0-110.8 ms | 111.9 ms | 112.0-113.8 ms |
| Full redraw, jiggler letter O | 542.0 ms | 544.1 ms | 544.8 ms |
| Full redraw, jiggler letter D | 368.9 ms | 370.7 ms | |
| Full redraw, watch | 229.9-232.5 ms | 233.1-234.5 ms | 235.6-237.2 ms |
| Control: 1.6.0 + one unused function | watch 234.9, clipboard 110.5, letter O 542.4 ms | | |

The last 1.7.0 row was measured after the review fixes. Those only changed core0 code (`usb_io.c`), yet the core1 full redraws moved by about 2 ms again, which shows the layout effect.

- **The frame rates are unchanged**, with and without the mirror stream.
- **A full redraw measures 0.4-3.5% slower (1-4 ms)**, which isn't visible. This is a page change, not a frame rate.
  - `ui_state_fill` was measured on the device at 20-34 µs per frame, so the sampling itself isn't the cause.
  - Code and data layout in the RP2040's 16 KB XIP flash cache moves these numbers by that much. Firmware 1.6.0 with one unused 64-word function added (a pure layout change) drew the watch 5 ms slower and the clipboard 0.5 ms slower.
- **Streaming the mirror (`WATCH 1`) adds at most 0.7 ms per full redraw.** That is the core0 check every 10 ms plus the USB writes.
