# Jiggler letter lanes, clipboard trash, tick-only mute, app mirror: design

Status: approved in conversation on 2026-09-28. Mockups were made with the brainstorm visual companion (`.superpowers/brainstorm/`, not committed).
Boards: RP2040-Touch-LCD-1.69 ("square") and ESP32-2424S012C ("round"). Both firmwares go to **1.6.0**.

## Goal

Make the board more pleasant to use every day:

- the jiggler traces a random sequence of outlined letters instead of a single circle;
- the clipboard can be cleared from the board;
- mute stops only the watch tick;
- the Windows app shows and controls all of this live.

## 1. Tick-only mute (square)

- `app.muted` silences only `buzzer_tick()` (the once-a-second watch tick).
- `feedback()`, the touch, swipe and button click, always sounds.
- Toggling mute always clicks. Today it clicks only when un-muting.

## 2. Page titles and clipboard layout (both boards)

- **Titles:** every page title ("Clipboard", "Jiggler", and "Settings" on the round board) moves up about 6 px. On the square the title centre moves from y 48 to 42; on the round, from y 46 to 40.
- **Clipboard order, top to bottom:** title, text box, Copy/Paste buttons, then the status line. The status line shows "Empty", "N chars from src", "Cleared", or the paste progress bar.

| | text box | buttons | status line |
|---|---|---|---|
| square | y 58..168 | y 176..220 | centre y 238 |
| round | y 56..136 | y 144..184 | centre y 198 |

- The paste progress bar moves to the status line's position, and `CLIP_ANIM` follows it.

## 3. Clipboard trash (both boards)

- **The icon:** a line-art bin. Square: at (196, 43), where the watch's mute icon sits. Round: at (178, 40), close to the title.
- **Enabled only when** there is text and no paste is typing. That's the same rule as Paste, plus "not while pasting". When disabled it's drawn in `C_FAINT`, and taps do nothing.
- **A tap:**
  - under `clip_mtx`, sets `clip_len = 0` and clears `clip_src`;
  - shows `app_message("Cleared")`, which appears in the status line;
  - plays the feedback click on the square.
- **Hit area:** about 36x36 px around the icon.
- **New icon:** `icon_trash` in `icons.c`, in both trees, which stay identical.

## 4. Letter-lane jiggler (both boards)

### Layout

- **Scale pill** (1.0X/1.5X/2.0X):
  - square: top-left (16..62, 33..51);
  - round: left of the letter (10..54, 111..129).
  - A tap does the same as the BOOT button on the jiggler page: cycle 1x → 1.5x → 2x and save.
- **ON/OFF pill:**
  - square: top-right (178..224, 33..51);
  - round: right of the letter (186..230, 111..129).
  - It's an indicator. A tap on it also toggles, as a convenience.
- **Letter box:**
  - square: 60..180 x 80..200;
  - round: about 73..167 x 66..164.
  - The ON/OFF tap zone is the letter box plus 10 px on each side.
- **Removed:** nothing is drawn inside the letter any more. "ON/OFF", "TAP TO START" and the scale pill are gone from the middle.
- **Bottom lines:** the status line ("Tap to start", "Next menu in Ns", …) and the stats line stay where they are.

### Lanes

- **What a letter is:** a centre-line polyline in a unit box, plus a `closed` flag.
- **Drawing:**
  - every segment as a thick capsule, at lane width + 2 × wall, in the accent colour (`C_SURF2` when off);
  - then every segment again at lane width, in `C_INNER`.
  - Round caps give round joins, and overlapping strokes (X, H) merge cleanly.
- **Size:** lane 18 px and wall 2 px on the square; lane 16 px on the round.

### Letters

- **The set:** O W M N Z X C V H J L B G D.
- **Loops:** O, B and D are closed and go round.
- **Bounces:** the rest are open, and the dot reverses at each end (ping-pong).
- **H and X** go back over their middle stroke, so each letter is one continuous polyline.

### One source of truth

- **The generator:** `tools/jigpaths.py` holds the letter definitions, the same pattern as `fontgen.py`. It writes:
  - `src/jig_paths.c/.h` and `esp32c3/src/jig_paths.c/.h`: per letter, a point list in 0..1000 box units, the point count, the closed flag, and the precomputed cumulative length;
  - `windows-app/src/TouchDeck.Core/Jiggler/JigPaths.g.cs`: the same data for the app.
- **Tests:** `tools/tests/test_jigpaths.py` checks:
  - that every lane, including its walls and the dot, stays inside its box at both boards' sizes;
  - the closed flags;
  - that the generated files are up to date.

### Motion

- **Speed:** the dot advances along the path by arc length at about 90 box-px/s, the same feel as today's roughly 4 s lap. The same ±30% sine drift applies. Bouncing letters reverse at the ends.
- **Sideways wander:** the lane offset is a smooth sum of two sines, capped at (lane/2 − dot radius), so the dot drifts within the lane like a car in its lane.
- **Mouse mapping:**
  - `target = anchor + k × scale × (dot − box centre)`, with k chosen so the letter box spans about 120 px of mouse movement at 1x;
  - it's sent as relative HID deltas with the existing accumulated-rounding scheme, so there's no drift;
  - the ±127 clamp per report still applies.
- **Anchoring:**
  - on `jiggler_set(true)`, the current mouse position is treated as the start point of the letter's path;
  - `anchor` is fixed from that and never changes while the jiggler stays on;
  - every letter is drawn around the same anchor, so the mouse always stays in the same area.

### Letter switching

- **When:** in `JIG_RESUME`, after the right-click → Esc sequence completes, a random letter different from the current one is chosen.
- **The glide:** the dot and mouse glide in a straight line from the current point to the new letter's start over about 500 ms (new sub-phase `JIG_GLIDE`), then continue on the new path.
- **What the screen shows:** always the active letter; it switches only when the glide starts.
- **Start and stop:** the first letter after turning on is random. When the jiggler is off, the last letter stays on screen, dimmed.

### Redraw cost (square and round)

- **Per animation frame:** only the dirty box around the dot's old and new positions is redrawn: their union, padded by the dot radius plus the anti-aliasing margin. It's drawn under `gfx_set_clip` and pushed with `lcd_push_rect`, the same as the watch hands.
- **Why:** redrawing the whole letter each frame would cost about 25 ms on the RP2040.
- **Full redraws** happen only on letter switch, on/off, scale and page change.
- **Status line:** the "Next menu in" line keeps its own anim rectangle.

### Scale on the round board

- It's new: the round board gains `JIG_SCALES` and `jig_scale_idx`, saved in `Preferences` key `jscale`.
- It's changed by tapping the pill. The ESP32 BOOT button driver stays unfinished and out of scope.

## 5. Protocol additions (both firmwares; `usb_io.c` / `link.cpp` header comments)

| Direction | Line | Meaning |
|---|---|---|
| PC → board | `WATCH 1` / `WATCH 0` | Start or stop `STATE` reports. Default is off, so `clip_helper.py` isn't flooded. |
| PC → board | `JIG ON` / `JIG OFF` | Turn the jiggler on or off (saved). |
| PC → board | `JIG SCALE <0-2>` | Set the scale index (saved). |
| PC → board | `CLIP CLEAR` | Same as the trash tap. Ignored while pasting. |
| board → PC | `STATE jig=<0\|1> letter=<L> scale=<0-2> phase=<n> x=<0-1000> y=<0-1000> clip=<len> paste=<0\|1>` | Sent at about 10 Hz while watched and the jiggler is on, otherwise on any change, and once right after `WATCH 1`. `x`/`y` are the dot in letter-box units. |

Old boards answer none of these. The app treats "no `STATE` within 1 s of `WATCH 1`" as older firmware.

## 6. Windows app

- **Core:** a `StateReport` record parsed by `BoardLine`, and new `DeviceSession` methods: `Watch(bool)`, `SetJiggler(bool)`, `SetScale(int)`, `ClearClip()`. The session sends `WATCH 1` after the handshake.
- **Jiggler card:**
  - the active letter's lane, drawn from `JigPaths.g.cs` as WPF strokes (a wide accent stroke, then a narrow inner one);
  - the live dot, moved at the `STATE` rate;
  - ON/OFF and scale pills that look like the device's;
  - the status text ("Next menu", "Right-click menu", …) from `phase`.
- **Board clip line:** "Board clip: N chars" with a trash button, disabled when N = 0 or while pasting. It goes on the Send card.
- **Older firmware:** the jiggler card is replaced by "Update the board's firmware to see and control the jiggler here", with the Install firmware button.
- **Versions:** app 1.1.0, bundling RP2040 firmware 1.6.0.

## 7. Testing

- **pytest:**
  - `test_jigpaths.py`: fit, flags, generated files up to date;
  - `test_fontgen.py`: the new label widths ("Cleared", the pill texts);
  - RP2040 board tests over COM6 for `WATCH`/`STATE`, `JIG ON/OFF/SCALE`, `CLIP CLEAR` (it only clears) and `BTN` on the jiggler page;
  - the same tests for the ESP32, skipping when it's absent;
  - ANIM demo perf: the jiggler frame must stay under 50 ms (`drawmax`).
- **C#:** `STATE` parsing, session commands, the old-firmware fallback, and the `JigPaths` table sanity (15 letters, closed flags).
- **Hardware and visual checks:**
  - app `--smoke` snapshot with the jiggler card live on the RP2040;
  - look at both boards;
  - the mouse stays within about 120 px (1x) of its start across several letter switches: check `STATE` x/y against the sent deltas over a dry-run session.

## 8. Docs

Update the root README, root CLAUDE.md, `windows-app/CLAUDE.md` and `windows-app/README.md` (screenshot included) for:

- the jiggler letters;
- the trash can;
- tick-only mute;
- the new protocol lines;
- the `jigpaths.py` generator.

## Out of scope (next spec)

The app update pipeline:

- GitHub Actions release builds;
- publishing to the public `SynexyConsulting/TouchDeckUpdates` repo's Releases with `updates.json`;
- the in-app update check;
- firmware downloads;
- code signing;
- ESP32 flashing from the app.

Also out of scope: the ESP32 BOOT button.
