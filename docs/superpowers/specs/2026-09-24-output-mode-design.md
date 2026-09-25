# Output mode (BT / PC) + round UI redesign: design spec

Status: design approved (mockup https://claude.ai/artifact/QbZAqAnnHaYKeZfYejQRWz), spec pending review
Target: `esp32c3/` (ESP32-2424S012C) first; the same design carries to the ESP32-S3-LCD-1.28 after.

## Goal
Every action that sends input to a computer (paste typing, jiggler moves, the jiggler's right-click and Esc) goes to exactly one place, chosen by a single **output mode**:
- **PC**: the board sends the action over the USB cable, and `tools/clip_helper.py` performs it on this PC.
- **BT**: the board acts as a Bluetooth LE keyboard and mouse for its paired host.

The mode is the only source of truth. The UI always shows the mode and whether its link is live.

Today's failure this fixes: output silently went to a paired iPhone, and nothing said so.

## Mode rules
- `app.mode` ∈ {PC, BT}, saved in `Preferences` key `mode`.
- BT is selectable only while a bond exists. With no bond, the Settings toggle shows BT locked and the mode is PC.
- At boot: saved BT but no bond → PC. Forget → PC.
- The link is live when: PC → the helper heartbeat is less than 5 s old; BT → `ble_ready()`.
- An action whose link is down doesn't run and says why ("Start the PC helper", "Waiting for Bluetooth").

## Board: output layer (new `output.cpp/.h`)
One interface that `typer` and `jiggler` call instead of `ble_*`:
```
bool out_ready();                      // link for the current mode is live
bool out_key(uint8_t mod, uint8_t usage);    // usage 0 = release all
bool out_mouse(uint8_t buttons, int8_t dx, int8_t dy);
bool out_caps_lock();
```
- BT sink → the existing `ble_key` / `ble_mouse`.
- PC sink → one line per report over serial (protocol below). The helper's Caps Lock state arrives as `LEDS`.
- Pacing per sink: BT keeps 12 ms key hold/gap and 15 ms mouse steps. PC uses 4 ms hold/gap and 15 ms mouse steps (serial is fast, and SendInput has no connection interval).
- Switching mode mid-action: a paste in progress is stopped, and the jiggler releases any held button or key on the old sink first.

## Protocol additions (board ↔ helper, line-based; existing lines unchanged)
Board → PC:
- `K <mod hex> <usage hex>`: a keyboard report, the same meaning as HID (`K 00 00` = all keys up).
- `M <buttons hex> <dx> <dy>`: a relative mouse report, the same meaning as HID.

PC → board:
- `LEDS <hex>`: host lock-key state (bit1 = Caps Lock). Sent on connect and whenever it changes.

## Helper (`tools/clip_helper.py`)
- Keyboard: map the HID usage to a PS/2 set-1 scancode, then SendInput with `KEYEVENTF_SCANCODE` (plus `EXTENDEDKEY` where needed). This behaves like a physical keyboard, so the board's typing stays the same US-layout logic in both modes. The modifier byte becomes Shift/Ctrl/Alt/GUI scancodes held around the key.
- Mouse: SendInput `MOUSEEVENTF_MOVE` (relative, subject to pointer acceleration like a real mouse), plus `RIGHTDOWN/UP`, `LEFTDOWN/UP` and `MIDDLEDOWN/UP` on button-bit changes.
- Safety: if the link drops or the helper exits, release all held keys and buttons.
- `--dry-run` logs what would be injected instead of calling SendInput, for testing without moving the real mouse.
- Poll Caps Lock (`GetKeyState(VK_CAPITAL)`) about every 250 ms and send `LEDS` on change.

## UI (render task), matching the mockup
- **Every page:** a 3 px ring at the screen edge, plus a top chip (icon, `PC`/`BLUETOOTH`, and a link dot). Colours: PC `#F2A33A` amber, BT `#4C8DFF` blue. The link dot is green `#3CCB7F` when the link is live and red `#E5484D` when it's down. Page dots at the bottom.
- **Clipboard:** title, a preview card (monospaced text), a meta line (or a progress bar while pasting), and pill buttons Copy (neutral) and Paste (accent fill, dark text). Paste becomes **Stop** (red) while typing.
- **Jiggler:** a tappable orbit. The ring and dot are in the accent colour when on, with ON/OFF in the centre and a "tap to start/stop" hint. Below it: a status line (red when the link is down) and a stats line.
- **Settings:** cog + title, "OUTPUT" label, a segmented Bluetooth|PC toggle (BT shows a lock when unpaired), a caption, and a Bluetooth row (icon, status line, chevron) that opens the Bluetooth sub-page.
- **Bluetooth sub-page:** a back button. Unpaired: icon, instructions, **Pair**. Pairing: the 6-digit PIN in boxes, the time until it expires, **Cancel**. Paired: a PC avatar with a status dot, CONNECTED/DISCONNECTED, the host name, Disconnect/Connect, and Forget. One page dot.
- **Rendering:** keep `gfx.c` (anti-aliased primitives) and add a filled pill (a rounded rect with radius = h/2 already covers it). Line icons (Bluetooth rune, monitor, copy, arrow, lock, chevrons, cog) are built from anti-aliased `gfx_line` strokes. Text uses the existing bitmap fonts, so the typography differs slightly from the mockup; layout, colour and hierarchy match.
- The host name is shown in plain ASCII: non-ASCII characters (e.g. the curly apostrophe in "Nik’s iPhone") are converted before drawing.

## Testing
- A `TAP x y` / `SWIPE L|R` serial debug command injects touch events into the same handler, so screen flows can be driven from a script without touching the board.
- The helper `--dry-run` plus a probe script checks that paste and the jiggler in PC mode emit the expected `K` and `M` lines, and that BT mode emits none.
- Visual checks on the device are done by the user (Claude can't see the screen).

## Out of scope (next)
- The ESP32-S3-LCD-1.28 port. That board has **no touchscreen**, so navigation there needs another input (BOOT button, or tilt/tap gestures on its QMI8658 motion sensor). It gets its own short design before porting.
- A native helper app (.exe / macOS).
