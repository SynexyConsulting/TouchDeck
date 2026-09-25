# Output Mode + Round UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every keystroke and mouse action goes where one output mode (PC or BT) says. The ESP32-C3 round UI is rebuilt to the approved mockup, and the RP2040 board is restyled to match.

**Architecture:** A new board-side output layer (`output.cpp`) routes the `typer` and `jiggler` reports either to the Bluetooth HID (`ble_hid.cpp`) or, as `K`/`M` serial lines, to `tools/clip_helper.py`. The helper performs them with Windows `SendInput` (new `tools/inject.py`). The UI reads the mode to colour everything: amber for PC, blue for BT.

**Tech Stack:** PlatformIO + Arduino-ESP32 2.0.17 (LovyanGFX, NimBLE-Arduino 1.4), Pico SDK 2.1.1 (RP2040), Python 3.9 + pyserial + ctypes + pytest.

**Spec:** `docs/superpowers/specs/2026-09-24-output-mode-design.md`

**Repo note:** the project isn't under git, so each task ends with a **Checkpoint** (build and tests green) instead of a commit.

## Global Constraints
- Accent colours: PC `#F2A33A`, BT `#4C8DFF`. Link live `#3CCB7F`, link down `#E5484D`. Background `#07090D`.
- The mode is saved in `Preferences` namespace `touchdeck`, key `mode` (0 = PC, 1 = BT).
- BT is selectable only while a bond exists. At boot, a saved BT with no bond becomes PC, and Forget sets PC.
- Protocol, board → PC: `K <mod hex2> <usage hex2>` and `M <buttons hex2> <dx dec> <dy dec>`. PC → board: `LEDS <hex2>` (bit1 = Caps Lock).
- Pacing: BT key hold/gap 12 ms, PC key hold/gap 4 ms, mouse step 15 ms for both.
- Everything drawn on the ESP32-C3 stays inside the 240 px circle. The device fonts are Font12 (7×12), Font16 (11×16) and Font24 (17×24), ASCII only.
- Flash the ESP32-C3: `cd esp32c3 && python -m platformio run -t upload --upload-port COM7`. Flash the RP2040: `python tools/flash.py`. Stop `clip_helper.py` first in both cases.

## Review Focus
1. **Switching mode mid-paste or mid-right-click:** no key or button is left held on the old destination, and the paste stops. The board side is pinned in Task 3. The helper side, where keys are released when the link drops, is pinned in Task 2.
2. **Malformed or unknown `K`/`M` lines** (bad hex, an unknown key code, a missing field): the helper ignores them and keeps running. Pinned in Task 2.
3. **A shifted key followed by an unshifted key without a release in between:** Shift must be released before the next key goes down, or it types the wrong character. Pinned in Task 1.
4. **Tapping Bluetooth in the toggle while unpaired:** the mode stays PC and nothing is sent over Bluetooth. Pinned in Task 3 (`MODE BT` rejected) and Task 4 (tap on the segment).
5. **A non-ASCII PC name** (e.g. a curly apostrophe): shown as ASCII, never garbled bytes. Pinned in Task 4 (`fold_ascii` host test).

---

### Task 1: Helper input injector (`tools/inject.py`)

**Files:**
- Create: `tools/inject.py`
- Test: `tools/tests/test_inject.py`

**Interfaces:**
- Produces: `Injector(dry_run=False, echo=True)` with `.key(mods:int, usage:int)`, `.mouse(buttons:int, dx:int, dy:int)`, `.release_all()`, `.caps_lock() -> bool`, and `.log` (a list of the event tuples sent: `("key", scancode, extended, up)`, `("move", dx, dy)`, `("button", name)`).

- [ ] **Step 1: Install pytest and write the failing tests**

Run: `python -m pip install --user pytest`

```python
# tools/tests/test_inject.py
import os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
from inject import Injector

def inj():
    return Injector(dry_run=True, echo=False)

def test_shifted_letter_press_and_release():
    i = inj()
    i.key(0x02, 0x04)                      # Shift + a  -> 'A'
    assert i.log == [("key", 0x2A, False, False), ("key", 0x1E, False, False)]
    i.log.clear()
    i.key(0x00, 0x00)                      # all up: key first, then Shift
    assert i.log == [("key", 0x1E, False, True), ("key", 0x2A, False, True)]

def test_escape_and_enter():
    i = inj()
    i.key(0, 0x29); i.key(0, 0); i.key(0, 0x28)
    assert ("key", 0x01, False, False) in i.log and ("key", 0x1C, False, False) in i.log

def test_shift_released_before_next_unshifted_key():
    i = inj()
    i.key(0x02, 0x04)                      # 'A' held
    i.log.clear()
    i.key(0x00, 0x05)                      # straight to 'b' with no release report
    assert i.log == [("key", 0x1E, False, True),   # a up
                     ("key", 0x2A, False, True),   # Shift up BEFORE b goes down
                     ("key", 0x30, False, False)]  # b down

def test_unknown_usage_is_ignored():
    i = inj()
    i.key(0, 0x99)
    assert i.log == []

def test_mouse_move_and_right_button_edges():
    i = inj()
    i.mouse(0, 5, -3)
    i.mouse(0x02, 0, 0)
    i.mouse(0x00, 0, 0)
    assert i.log == [("move", 5, -3), ("button", "rightdown"), ("button", "rightup")]

def test_release_all_lets_go_of_everything():
    i = inj()
    i.key(0x02, 0x04); i.mouse(0x02, 0, 0)
    i.log.clear()
    i.release_all()
    assert ("key", 0x1E, False, True) in i.log
    assert ("key", 0x2A, False, True) in i.log
    assert ("button", "rightup") in i.log

@pytest.mark.skipif(sys.platform != "win32", reason="Windows only")
def test_input_struct_matches_win64_layout():
    import inject
    assert inject.ctypes.sizeof(inject.INPUT) == (40 if sys.maxsize > 2**32 else 28)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `python -m pytest tools/tests/test_inject.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'inject'`

- [ ] **Step 3: Write the implementation**

```python
# tools/inject.py
"""Performs Touch Deck keyboard/mouse reports on this PC (PC output mode).

The board sends HID-style reports over serial (K <mod> <usage>, M <buttons> <dx> <dy>).
This turns them into Windows SendInput events. Keys go in as scancodes, so they
behave exactly like a physical keyboard and the board's US-layout typing logic
is the same in both output modes.
"""
import ctypes
import sys

# HID keyboard usage -> (PS/2 set-1 scancode, extended-key flag)
_LETTERS = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C]
SCANCODES = {0x04 + i: (sc, False) for i, sc in enumerate(_LETTERS)}      # a..z
SCANCODES.update({0x1E + i: (0x02 + i, False) for i in range(10)})        # 1..9, 0
SCANCODES.update({
    0x28: (0x1C, False),  # Enter
    0x29: (0x01, False),  # Esc
    0x2A: (0x0E, False),  # Backspace
    0x2B: (0x0F, False),  # Tab
    0x2C: (0x39, False),  # Space
    0x2D: (0x0C, False), 0x2E: (0x0D, False), 0x2F: (0x1A, False), 0x30: (0x1B, False),  # - = [ ]
    0x31: (0x2B, False), 0x33: (0x27, False), 0x34: (0x28, False), 0x35: (0x29, False),  # \ ; ' `
    0x36: (0x33, False), 0x37: (0x34, False), 0x38: (0x35, False),                       # , . /
})
# HID modifier bit -> (scancode, extended)
MODIFIERS = [(0x01, 0x1D, False), (0x02, 0x2A, False), (0x04, 0x38, False), (0x08, 0x5B, True),
             (0x10, 0x1D, True), (0x20, 0x36, False), (0x40, 0x38, True), (0x80, 0x5C, True)]
# HID mouse button bit -> (down event, up event)
BUTTONS = [(0x01, "leftdown", "leftup"), (0x02, "rightdown", "rightup"),
           (0x04, "middledown", "middleup")]


class Injector:
    def __init__(self, dry_run=False, echo=True):
        self.dry_run = dry_run
        self.echo = echo
        self.log = []
        self.held_key = 0
        self.held_mods = 0
        self.held_buttons = 0

    def key(self, mods, usage):
        events = []
        if self.held_key and self.held_key != usage:              # 1. old key up
            events.append(("key", *SCANCODES[self.held_key], True))
            self.held_key = 0
        for bit, sc, ext in MODIFIERS:                            # 2. dropped modifiers up
            if self.held_mods & bit and not mods & bit:
                events.append(("key", sc, ext, True))
        for bit, sc, ext in MODIFIERS:                            # 3. new modifiers down
            if mods & bit and not self.held_mods & bit:
                events.append(("key", sc, ext, False))
        self.held_mods = mods
        if usage and usage != self.held_key and usage in SCANCODES:  # 4. new key down
            events.append(("key", *SCANCODES[usage], False))
            self.held_key = usage
        self._send(events)

    def mouse(self, buttons, dx, dy):
        events = []
        if dx or dy:
            events.append(("move", dx, dy))
        for bit, down, up in BUTTONS:
            if buttons & bit and not self.held_buttons & bit:
                events.append(("button", down))
            elif self.held_buttons & bit and not buttons & bit:
                events.append(("button", up))
        self.held_buttons = buttons
        self._send(events)

    def release_all(self):
        self.key(0, 0)
        self.mouse(0, 0, 0)

    def caps_lock(self):
        if self.dry_run or sys.platform != "win32":
            return False
        return bool(ctypes.windll.user32.GetKeyState(0x14) & 1)

    def _send(self, events):
        if not events:
            return
        self.log.extend(events)
        if self.echo:
            for e in events:
                print(f"  inject: {e}")
        if not self.dry_run:
            _send_input(events)


if sys.platform == "win32":
    from ctypes import wintypes

    ULONG_PTR = ctypes.c_size_t

    class MOUSEINPUT(ctypes.Structure):
        _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD),
                    ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]

    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                    ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]

    class HARDWAREINPUT(ctypes.Structure):
        _fields_ = [("uMsg", wintypes.DWORD), ("wParamL", wintypes.WORD), ("wParamH", wintypes.WORD)]

    class _INPUTUNION(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]

    class INPUT(ctypes.Structure):
        _fields_ = [("type", wintypes.DWORD), ("u", _INPUTUNION)]

    _MOUSE_FLAGS = {"leftdown": 0x02, "leftup": 0x04, "rightdown": 0x08, "rightup": 0x10,
                    "middledown": 0x20, "middleup": 0x40}

    def _send_input(events):
        arr = (INPUT * len(events))()
        for i, e in enumerate(events):
            if e[0] == "key":
                _, sc, ext, up = e
                arr[i].type = 1                                   # INPUT_KEYBOARD
                flags = 0x0008 | (0x0001 if ext else 0) | (0x0002 if up else 0)  # SCANCODE|EXTENDED|KEYUP
                arr[i].u.ki = KEYBDINPUT(0, sc, flags, 0, 0)
            elif e[0] == "move":
                arr[i].type = 0                                   # INPUT_MOUSE
                arr[i].u.mi = MOUSEINPUT(e[1], e[2], 0, 0x0001, 0, 0)   # MOVE (relative)
            else:
                arr[i].type = 0
                arr[i].u.mi = MOUSEINPUT(0, 0, 0, _MOUSE_FLAGS[e[1]], 0, 0)
        ctypes.windll.user32.SendInput(len(events), arr, ctypes.sizeof(INPUT))
else:
    def _send_input(events):
        raise RuntimeError("PC output mode needs Windows SendInput")
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `python -m pytest tools/tests/test_inject.py -v`
Expected: 7 passed

- [ ] **Step 5: Checkpoint.** Tests green.

---

### Task 2: Helper protocol (`K`, `M`, `LEDS`, `--dry-run`)

**Files:**
- Modify: `tools/clip_helper.py` (`run()`, `main()`, new `handle_line()`)
- Test: `tools/tests/test_helper.py`

**Interfaces:**
- Consumes: `Injector` from Task 1.
- Produces: `handle_line(line:str, ser, inj) -> None`, plus the serial lines `LEDS 00`/`LEDS 02` sent to the board.

- [ ] **Step 1: Write the failing tests**

```python
# tools/tests/test_helper.py
import os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import clip_helper
from inject import Injector

class FakeSerial:
    def __init__(self): self.out = b""
    def write(self, b): self.out += b

def setup():
    return FakeSerial(), Injector(dry_run=True, echo=False)

def test_key_line_becomes_key_report():
    ser, inj = setup()
    clip_helper.handle_line("K 02 04", ser, inj)
    assert inj.held_key == 0x04 and inj.held_mods == 0x02

def test_mouse_line_parses_signed_decimal():
    ser, inj = setup()
    clip_helper.handle_line("M 00 -7 12", ser, inj)
    assert inj.log == [("move", -7, 12)]

def test_malformed_lines_are_ignored():
    ser, inj = setup()
    for bad in ["K zz 04", "K 02", "M 00 x 1", "M", "K 00 99"]:
        clip_helper.handle_line(bad, ser, inj)   # must not raise
    assert inj.log == []

def test_copy_request_sends_clip(monkeypatch):
    ser, inj = setup()
    monkeypatch.setattr(clip_helper, "grab_text", lambda: ("hi", "select"))
    clip_helper.handle_line("COPY", ser, inj)
    assert ser.out == b"CLIP 2 select\nhi"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `python -m pytest tools/tests/test_helper.py -v`
Expected: FAIL with `AttributeError: module 'clip_helper' has no attribute 'handle_line'`

- [ ] **Step 3: Implement.** In `tools/clip_helper.py`, add `from inject import Injector` beside the other imports, then add this above `run()`:

```python
def handle_line(line, ser, inj):
    """One line from the board. K/M are input reports for PC output mode."""
    parts = line.split()
    try:
        if line == "COPY":
            print("COPY requested")
            send_clip(ser, *grab_text())
        elif line.startswith("LOG "):
            print(time.strftime("%H:%M:%S ") + f"board: {line[4:]}")
        elif parts[0] == "K" and len(parts) == 3:
            inj.key(int(parts[1], 16), int(parts[2], 16))
        elif parts[0] == "M" and len(parts) == 4:
            dx = max(-127, min(127, int(parts[2])))
            dy = max(-127, min(127, int(parts[3])))
            inj.mouse(int(parts[1], 16), dx, dy)
    except (ValueError, IndexError):
        print(f"  ignored malformed line: {line!r}")
```

Replace `run()` with:

```python
def run(debug=False, dry_run=False):
    inj = Injector(dry_run=dry_run, echo=dry_run)
    while True:
        ser = open_board()
        print(f"Connected on {ser.port}" + ("  (dry run: input is logged, not performed)" if dry_run else ""))
        try:
            ser.write(b"\nHELLO\n")
            send_time(ser)
            last_ping = last_time = last_caps_poll = time.time()
            caps = None                      # forces an initial LEDS
            buf = b""
            while True:
                buf += ser.read(256)
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    handle_line(line.strip().decode(errors="replace"), ser, inj)
                now = time.time()
                if now - last_caps_poll >= 0.25:
                    last_caps_poll = now
                    c = inj.caps_lock()
                    if c != caps:
                        caps = c
                        ser.write(b"LEDS 02\n" if c else b"LEDS 00\n")
                if now - last_ping >= PING_S:
                    ser.write(b"DBG\n" if debug else b"PING\n")
                    last_ping = now
                if now - last_time >= 3600:
                    send_time(ser)
                    last_time = now
        except (serial.SerialException, OSError):
            print("Disconnected")
            ser.close()
            time.sleep(1)
        finally:
            inj.release_all()                # never leave a key or button held on the PC
```

In `main()`, add `ap.add_argument("--dry-run", action="store_true", help="log PC-mode keystrokes/mouse instead of performing them")` and change the call to `run(args.debug, args.dry_run)`. Remove the now-duplicated `COPY`/`LOG` branches from the old loop (they live in `handle_line`).

- [ ] **Step 4: Run all helper tests**

Run: `python -m pytest tools/tests -v`
Expected: 11 passed (7 inject + 4 helper)

- [ ] **Step 5: Checkpoint.** Tests green.

---

### Task 3: Board output layer, saved mode, scripted touch

**Files:**
- Create: `esp32c3/src/output.h`, `esp32c3/src/output.cpp`
- Modify: `esp32c3/src/app.h` (add `pc_leds`), `esp32c3/src/ble_hid.h/.cpp` (export `ble_bonded()`), `esp32c3/src/typer.cpp`, `esp32c3/src/jiggler.h/.cpp`, `esp32c3/src/link.cpp`, `esp32c3/src/main.cpp`
- Test: `tools/tests/test_board_pc_mode.py` (hardware-in-the-loop; skipped when the board isn't connected)

**Interfaces:**
- Consumes: `ble_key`, `ble_mouse`, `ble_ready`, `ble_caps_lock` (existing), `link_send_line` (existing).
- Produces (`output.h`):
  ```cpp
  enum out_mode_t { MODE_PC = 0, MODE_BT = 1 };
  void mode_init();
  out_mode_t mode_get();
  bool mode_bt_available();
  bool mode_set(out_mode_t m);
  bool out_ready();
  const char *out_down_reason();
  bool out_key(uint8_t mod, uint8_t usage);
  bool out_mouse(uint8_t buttons, int8_t dx, int8_t dy);
  bool out_caps_lock();
  uint32_t out_key_pace_ms();
  ```
  Also `void jiggler_on_output_change();` (jiggler.h), `bool ble_bonded();` (ble_hid.h), `void inject_touch(int type, int x, int y);` (main.cpp), and the serial debug commands `MODE PC|BT`, `TAP x y`, `SWIPE L|R`, with `DBG` gaining ` mode=%d jig=%d leds=%02X`.

- [ ] **Step 1: Write the failing hardware test**

```python
# tools/tests/test_board_pc_mode.py
"""Drives the ESP32-C3 over USB serial, acting as the helper, and checks that
PC output mode emits K/M lines. Nothing is performed on the PC."""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
from touchdeck import ESP_PID, ESP_VID, find_port, open_serial

PORT = find_port(ESP_VID, ESP_PID)
pytestmark = pytest.mark.skipif(PORT is None, reason="ESP32-C3 Touch Deck not connected")

class Board:
    def __init__(self):
        self.s = open_serial(PORT, timeout=0.02)
        self.buf, self.lines, self.last_ping = b"", [], 0
        self.send("HELLO")
        self.pump(0.3)

    def send(self, line):
        self.s.write((line + "\n").encode())

    def pump(self, secs):
        end = time.time() + secs
        while time.time() < end:
            if time.time() - self.last_ping > 1:
                self.send("PING"); self.last_ping = time.time()
            self.buf += self.s.read(512)
            while b"\n" in self.buf:
                l, self.buf = self.buf.split(b"\n", 1)
                self.lines.append(l.decode(errors="replace").strip())

    def take(self):
        out, self.lines = self.lines, []
        return out

    def dbg(self):
        self.take(); self.send("DBG"); self.pump(0.4)
        return next(l for l in self.take() if "up=" in l)

    def field(self, name):
        d = self.dbg()
        return d.split(f" {name}=")[1].split()[0]

    def goto(self, screen):
        for _ in range(3): self.send("SWIPE R"); self.pump(0.1)
        for _ in range(screen): self.send("SWIPE L"); self.pump(0.1)

@pytest.fixture
def board():
    b = Board()
    b.send("MODE PC"); b.pump(0.2)
    if b.field("jig") == "1":            # start from a known state
        b.goto(1); b.send("TAP 120 115"); b.pump(0.3)
    yield b
    if b.field("jig") == "1":
        b.goto(1); b.send("TAP 120 115"); b.pump(0.3)
    b.s.close()

def test_jiggler_in_pc_mode_sends_mouse_reports(board):
    board.goto(1)
    board.take(); board.send("TAP 120 115"); board.pump(1.5)
    moves = [l for l in board.take() if l.startswith("M ")]
    assert len(moves) >= 20
    board.send("TAP 120 115"); board.pump(0.3); board.take()
    board.pump(1.0)
    assert not [l for l in board.take() if l.startswith("M ") and l != "M 00 0 0"]

def test_paste_in_pc_mode_types_key_reports(board):
    board.goto(0)
    board.s.write(b"CLIP 3 cli\nHi\n"); board.pump(0.3)
    board.take(); board.send("TAP 160 186"); board.pump(1.0)
    keys = [l for l in board.take() if l.startswith("K ")]
    assert keys == ["K 02 0B", "K 00 00", "K 00 0C", "K 00 00", "K 00 28", "K 00 00"]

def test_bluetooth_mode_refused_while_unpaired(board):
    d = board.dbg()
    if " state=0 " not in d:
        pytest.skip("board has a Bluetooth bond; Forget it to run this check")
    board.send("MODE BT"); board.pump(0.3)
    assert board.field("mode") == "0"
```

- [ ] **Step 2: Run it to verify it fails**

Run (stop `clip_helper.py` first): `python -m pytest tools/tests/test_board_pc_mode.py -v`
Expected: FAIL. The current firmware ignores `MODE`/`TAP`/`SWIPE`, so `field("jig")` raises `IndexError`.

- [ ] **Step 3: Add the output layer**

```cpp
// esp32c3/src/output.h
// Output routing: every keystroke / mouse report goes through here, and the
// output mode decides where it lands (spec: docs/superpowers/specs/2026-09-24-output-mode-design.md).
//   PC: "K"/"M" lines over USB serial; tools/clip_helper.py performs them.
//   BT: Bluetooth LE HID reports to the bonded host.
#pragma once
#include <stdint.h>

enum out_mode_t { MODE_PC = 0, MODE_BT = 1 };

void mode_init();                   // load the saved mode; call after ble_init()
out_mode_t mode_get();              // effective mode: BT only while bonded
bool mode_bt_available();           // a Bluetooth bond exists
bool mode_set(out_mode_t m);        // false (no change) if BT isn't available

bool out_ready();                   // the current mode's link is live
const char *out_down_reason();      // why out_ready() is false, for the UI
bool out_key(uint8_t mod, uint8_t usage);            // usage 0 = release all
bool out_mouse(uint8_t buttons, int8_t dx, int8_t dy);
bool out_caps_lock();
uint32_t out_key_pace_ms();         // key hold and gap for the current sink
```

```cpp
// esp32c3/src/output.cpp
#include <Arduino.h>
#include <Preferences.h>
#include "app.h"
#include "ble_hid.h"
#include "jiggler.h"
#include "link.h"
#include "output.h"
#include "typer.h"

static Preferences prefs;
static out_mode_t wanted = MODE_PC;   // the user's choice (saved)
static bool key_held[2];              // a non-release key report is outstanding, per sink
static uint8_t buttons_held[2];

void mode_init() {
    prefs.begin("touchdeck", false);
    wanted = prefs.getUChar("mode", MODE_PC) == MODE_BT ? MODE_BT : MODE_PC;
    if (wanted == MODE_BT && !ble_bonded()) {   // bond gone since last run
        wanted = MODE_PC;
        prefs.putUChar("mode", MODE_PC);
    }
}

bool mode_bt_available() { return ble_bonded(); }

out_mode_t mode_get() { return (wanted == MODE_BT && ble_bonded()) ? MODE_BT : MODE_PC; }

static bool sink_key(out_mode_t m, uint8_t mod, uint8_t usage) {
    bool ok;
    if (m == MODE_BT) {
        ok = ble_key(mod, usage);
    } else {
        if (!app.helper) return false;
        char s[16];
        snprintf(s, sizeof s, "K %02X %02X", usage ? mod : 0, usage);
        link_send_line(s);
        ok = true;
    }
    if (ok) key_held[m] = usage != 0;
    return ok;
}

static bool sink_mouse(out_mode_t m, uint8_t buttons, int8_t dx, int8_t dy) {
    bool ok;
    if (m == MODE_BT) {
        ok = ble_mouse(buttons, dx, dy);
    } else {
        if (!app.helper) return false;
        char s[24];
        snprintf(s, sizeof s, "M %02X %d %d", buttons, dx, dy);
        link_send_line(s);
        ok = true;
    }
    if (ok) buttons_held[m] = buttons;
    return ok;
}

static void release_all(out_mode_t m) {
    if (key_held[m]) sink_key(m, 0, 0);
    if (buttons_held[m]) sink_mouse(m, 0, 0, 0);
}

bool mode_set(out_mode_t m) {
    if (m == MODE_BT && !ble_bonded()) return false;
    out_mode_t old = mode_get();
    wanted = m;
    prefs.putUChar("mode", m);
    if (mode_get() != old) {
        if (typer_busy()) typer_cancel();
        release_all(old);                 // nothing stays held on the old destination
        jiggler_on_output_change();
    }
    app_redraw();
    return true;
}

bool out_ready() { return mode_get() == MODE_BT ? ble_ready() : app.helper; }

const char *out_down_reason() {
    return mode_get() == MODE_BT ? "Waiting for Bluetooth" : "Start the PC helper";
}

bool out_key(uint8_t mod, uint8_t usage) { return sink_key(mode_get(), mod, usage); }
bool out_mouse(uint8_t buttons, int8_t dx, int8_t dy) { return sink_mouse(mode_get(), buttons, dx, dy); }
bool out_caps_lock() { return mode_get() == MODE_BT ? ble_caps_lock() : (app.pc_leds & 0x02); }
uint32_t out_key_pace_ms() { return mode_get() == MODE_BT ? 12 : 4; }
```

- [ ] **Step 4: Wire it in**

- `app.h`: add `volatile uint8_t pc_leds;          // helper's lock-key state (LEDS)` under `helper`.
- `ble_hid.h`: add `bool ble_bonded();          // a bond exists`. In `ble_hid.cpp`, rename `static bool bonded()` to `bool ble_bonded()` and update its four call sites.
- `typer.cpp`: add `#include "output.h"` and replace `#include "ble_hid.h"` with it. Delete `#define HOLD_MS 12` / `#define GAP_MS 12` and use `out_key_pace_ms()` in their two places. Replace `ble_ready()` with `out_ready()`, `ble_key(` with `out_key(`, `ble_caps_lock()` with `out_caps_lock()`, and `KEY_MOD_LSHIFT` with `0x02`. Change the start message to `app_message(out_down_reason());` and the mid-paste loss message to `app_message(out_down_reason());`.
- `jiggler.cpp`: same substitutions (`ble_ready`→`out_ready`, `ble_mouse`→`out_mouse`, `ble_key`→`out_key`, `MOUSE_BTN_RIGHT`→`0x02`, include `output.h` instead of `ble_hid.h`), and add:
  ```cpp
  // The old destination's right-click/Esc sequence is abandoned (output.cpp
  // already released anything held there); start a fresh cycle.
  void jiggler_on_output_change() {
      if (!app.jig_on) return;
      app.jig_phase = JIG_CIRCLE;
      schedule_menu();
      next_ms = now_ms();
  }
  ```
  and declare `void jiggler_on_output_change();` in `jiggler.h`.
- `link.cpp`: add `#include "output.h"` and `#include "touch.h"`, `extern void inject_touch(int type, int x, int y);`, and these branches before `DBG`:
  ```cpp
  } else if (!strncmp(s, "LEDS ", 5)) {
      app.pc_leds = (uint8_t)strtol(s + 5, nullptr, 16);
  } else if (!strncmp(s, "MODE ", 5)) {       // scripting / tests
      mode_set(!strcmp(s + 5, "BT") ? MODE_BT : MODE_PC);
  } else if (!strncmp(s, "TAP ", 4)) {        // scripting / tests: inject a tap
      int x, y;
      if (sscanf(s + 4, "%d %d", &x, &y) == 2) inject_touch(EV_TAP, x, y);
  } else if (!strcmp(s, "SWIPE L")) {
      inject_touch(EV_SWIPE_L, 120, 120);
  } else if (!strcmp(s, "SWIPE R")) {
      inject_touch(EV_SWIPE_R, 120, 120);
  ```
  and document `LEDS`, `MODE`, `TAP`, `SWIPE`, `K`, `M` in the protocol comment at the top.
- `main.cpp`: `#include "output.h"`. Call `mode_init();` right after `ble_init();`. Add after `on_touch`:
  ```cpp
  // Serial TAP/SWIPE commands land here, so flows can be scripted.
  void inject_touch(int type, int x, int y) {
      touch_event_t e = {(touch_ev_t)type, x, y};
      on_touch(e);
  }
  ```
  In `debug_report`, append ` mode=%d jig=%d leds=%02X` fed by `(int)mode_get(), app.jig_on, app.pc_leds`. In the BT Forget branch of `on_touch_bt`, call `mode_set(MODE_PC);` after `ble_forget();`.

- [ ] **Step 5: Build, flash, run the tests**

Run: `cd esp32c3 && python -m platformio run -t upload --upload-port COM7`, then `python -m pytest tools/tests -v`
Expected: all pass (the Bluetooth-refused test skips if the board still has a bond).

- [ ] **Step 6: Checkpoint.** Add to `CLAUDE.md`'s ESP32-C3 section: the output layer and mode rules, the `K`/`M`/`LEDS`/`MODE`/`TAP`/`SWIPE` lines, and `python -m pytest tools/tests -v` (hardware tests skip with no board).

---

### Task 4: ESP32-C3 round UI to the approved mockup

**Files:**
- Create: `esp32c3/src/icons.h`, `esp32c3/src/icons.c` (plain C so the RP2040 can reuse it)
- Modify: `esp32c3/src/ui.h` (geometry), `esp32c3/src/ui.cpp` (rewrite), `esp32c3/src/main.cpp` (touch handling), `esp32c3/src/ble_hid.cpp` (`fold_ascii` for the host name)
- Test: `tools/tests/test_board_ui_flow.py`

**Interfaces:**
- Consumes: `mode_get`, `mode_set`, `mode_bt_available`, `out_ready`, `out_down_reason` (Task 3), and the `ble_state`/`ble_*` functions (existing).
- Produces: `icons.h`: `typedef void (*icon_fn)(float cx, float cy, float size, uint16_t col);` plus `icon_bt`, `icon_monitor`, `icon_copy`, `icon_arrow_right`, `icon_lock`, `icon_chevron_left`, `icon_chevron_right`, `icon_cog`, all with the `icon_fn` signature.

- [ ] **Step 1: Write the failing UI-flow test**

```python
# tools/tests/test_board_ui_flow.py
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from test_board_pc_mode import Board, PORT, pytestmark  # noqa: F401  (same skip rule)
import pytest

@pytest.fixture
def board():
    b = Board(); yield b; b.s.close()

def test_settings_toggle_and_bluetooth_drilldown(board):
    board.goto(2)
    assert board.field("screen") == "2"
    board.send("TAP 160 94"); board.pump(0.2)          # PC segment
    assert board.field("mode") == "0"
    board.send("TAP 120 162"); board.pump(0.2)         # Bluetooth row
    assert board.field("bt_page") == "1"
    board.send("TAP 54 48"); board.pump(0.2)           # back chevron
    assert board.field("bt_page") == "0"

def test_bluetooth_segment_locked_when_unpaired(board):
    if " state=0 " not in board.dbg():
        pytest.skip("board has a Bluetooth bond")
    board.goto(2)
    board.send("TAP 80 94"); board.pump(0.2)
    assert board.field("mode") == "0"

def test_host_name_is_ascii(board):
    d = board.dbg()
    host = d.split(" host=")[1].split(" | ")[0]
    assert all(32 <= ord(c) < 127 for c in host)
```

- [ ] **Step 2: Run it to verify it fails**

Run: `python -m pytest tools/tests/test_board_ui_flow.py -v`
Expected: `test_settings_toggle_and_bluetooth_drilldown` FAILS: the old Settings page opens Bluetooth on a cog tap at (120,162), and `TAP 54 48` doesn't hit the old back area. `test_host_name_is_ascii` FAILS while the host is the iPhone name with a curly apostrophe (skip condition: none).

- [ ] **Step 3: Icons**

```c
// esp32c3/src/icons.h
// Line icons drawn from anti-aliased strokes on a 24-unit grid centred on
// (cx, cy). Shared by the ESP32-C3 and RP2040 UIs.
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef void (*icon_fn)(float cx, float cy, float size, uint16_t col);
void icon_bt(float cx, float cy, float size, uint16_t col);
void icon_monitor(float cx, float cy, float size, uint16_t col);
void icon_copy(float cx, float cy, float size, uint16_t col);
void icon_arrow_right(float cx, float cy, float size, uint16_t col);
void icon_lock(float cx, float cy, float size, uint16_t col);
void icon_chevron_left(float cx, float cy, float size, uint16_t col);
void icon_chevron_right(float cx, float cy, float size, uint16_t col);
void icon_cog(float cx, float cy, float size, uint16_t col);   // hole is background-black
#ifdef __cplusplus
}
#endif
```

```c
// esp32c3/src/icons.c
#include "icons.h"
#include "gfx.h"

static float K, CX, CY, W;
static uint16_t COL;

static void begin(float cx, float cy, float size, uint16_t col) {
    K = size / 24.f; CX = cx; CY = cy; COL = col;
    W = K * 2.4f < 1.4f ? 1.4f : K * 2.4f;
}
static void seg(float x0, float y0, float x1, float y1) {
    gfx_line(CX + (x0 - 12) * K, CY + (y0 - 12) * K, CX + (x1 - 12) * K, CY + (y1 - 12) * K, W, COL);
}

void icon_bt(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(7, 7, 17, 17); seg(17, 17, 12, 22); seg(12, 22, 12, 2); seg(12, 2, 17, 7); seg(17, 7, 7, 17);
}
void icon_monitor(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(2, 4, 22, 4); seg(22, 4, 22, 17); seg(22, 17, 2, 17); seg(2, 17, 2, 4);
    seg(8, 21, 16, 21); seg(12, 17, 12, 21);
}
void icon_copy(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(8, 8, 21, 8); seg(21, 8, 21, 21); seg(21, 21, 8, 21); seg(8, 21, 8, 8);
    seg(16, 8, 16, 3); seg(16, 3, 3, 3); seg(3, 3, 3, 16); seg(3, 16, 8, 16);
}
void icon_arrow_right(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(5, 12, 19, 12); seg(13, 6, 19, 12); seg(19, 12, 13, 18);
}
void icon_lock(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    gfx_rrect((int)(cx - 7 * K), (int)(cy - 1 * K), (int)(14 * K + 1), (int)(10 * K + 1), 2 * K, c);
    seg(8, 11, 8, 8); seg(8, 8, 10, 4.5f); seg(10, 4.5f, 14, 4.5f); seg(14, 4.5f, 16, 8); seg(16, 8, 16, 11);
}
void icon_chevron_left(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(15, 6, 9, 12); seg(9, 12, 15, 18);
}
void icon_chevron_right(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(9, 6, 15, 12); seg(15, 12, 9, 18);
}
void icon_cog(float cx, float cy, float s, uint16_t c) {
    float k = s / 24.f;
    for (int i = 0; i < 8; i++) {
        float a = i * 0.7854f;
        gfx_line(cx + cosf(a) * 6.f * k, cy + sinf(a) * 6.f * k,
                 cx + cosf(a) * 10.5f * k, cy + sinf(a) * 10.5f * k, 3.4f * k, c);
    }
    gfx_disc(cx, cy, 7.5f * k, c);
    gfx_disc(cx, cy, 3.f * k, RGB(7, 9, 13));
}
```
(`icons.c` also needs `#include <math.h>` for `cosf`/`sinf`.)

- [ ] **Step 4: Geometry (`ui.h` replaces its hit-area block)**

```c
// Clipboard
#define BTN_Y       166
#define BTN_H       40
#define BTN_W       79
#define BTN_COPY_X  38
#define BTN_PASTE_X 123
// Jiggler
#define JIG_CX 120
#define JIG_CY 120
#define JIG_R  57
// Settings
#define SEG_Y     77
#define SEG_H     34
#define SEG_W     78
#define SEG_BT_X  41
#define SEG_PC_X  121
#define ROW_X     34
#define ROW_Y     140
#define ROW_W     172
#define ROW_H     44
// Bluetooth page
#define BACK_CX    54
#define BACK_CY    48
#define BACK_HIT_R 22
#define BT_BTN1_X  66
#define BT_BTN1_Y  176
#define BT_BTN1_W  108
#define BT_BTN1_H  36
#define BT_BTN2_Y  178
#define BT_BTN2_H  34
#define BT_BTN2_W  77
#define BT_BTN_L_X 40
#define BT_BTN_R_X 123
```

- [ ] **Step 5: Rewrite `ui.cpp`**

```cpp
// Round-screen UI, built to the approved mockup
// (https://claude.ai/artifact/QbZAqAnnHaYKeZfYejQRWz). One accent colour
// carries the output mode everywhere: amber = PC, blue = Bluetooth.
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "app.h"
#include "ble_hid.h"
#include "board.h"
#include "display.h"
#include "gfx.h"
#include "icons.h"
#include "output.h"
#include "ui.h"

#define C_BG      RGB(7, 9, 13)
#define C_SURF    RGB(20, 26, 36)
#define C_SURF2   RGB(28, 36, 50)
#define C_INNER   RGB(16, 21, 30)
#define C_TEXT    RGB(232, 236, 242)
#define C_CLIP    RGB(201, 209, 220)
#define C_DIM     RGB(138, 148, 166)
#define C_FAINT   RGB(74, 83, 102)
#define C_DOT_OFF RGB(58, 66, 82)
#define C_BT      RGB(76, 141, 255)
#define C_PC      RGB(242, 163, 58)
#define C_OK      RGB(60, 203, 127)
#define C_BAD     RGB(229, 72, 77)
#define C_BAD_TXT RGB(255, 138, 141)
#define C_BT_TINT RGB(18, 30, 52)    // 16% blue over C_BG
#define C_PC_TINT RGB(45, 34, 20)    // 16% amber over C_BG

static uint16_t accent() { return mode_get() == MODE_BT ? C_BT : C_PC; }

static void text_c(int y, const char *s, const sFONT *f, uint16_t col) {
    gfx_text_centered(LCD_W / 2, y, s, f, col);
}

static void pill(int x, int y, int w, int h, uint16_t col) { gfx_rrect(x, y, w, h, h / 2.f, col); }

// Pill button: optional leading/trailing icon around a centred label.
static void button(int x, int y, int w, int h, uint16_t bg, uint16_t fg, const char *label,
                   const sFONT *f, icon_fn lead, icon_fn trail) {
    pill(x, y, w, h, bg);
    const int iw = 12, gap = 4, tw = (int)strlen(label) * f->Width;
    int total = tw + (lead ? iw + gap : 0) + (trail ? iw + gap : 0);
    int cx = x + (w - total) / 2, cy = y + h / 2;
    if (lead) { lead(cx + iw / 2.f, cy, iw, fg); cx += iw + gap; }
    gfx_text(cx, cy - f->Height / 2, label, f, fg);
    cx += tw + gap;
    if (trail) trail(cx + iw / 2.f, cy, iw, fg);
}

// Transient message (app_message) if one is showing, else the given text.
static const char *msg_or(const char *normal, char *buf, int n) {
    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    bool show = (int32_t)(app.msg_until_ms - now_ms()) > 0;
    if (show) { strncpy(buf, app.msg, n - 1); buf[n - 1] = 0; }
    xSemaphoreGive(clip_mtx);
    return show ? buf : normal;
}

// Edge ring + top chip: where output goes, and whether that link is live.
static void frame() {
    gfx_fill(C_BG);
    gfx_ring(120.f, 120.f, 118.5f, 3.f, accent());
    bool bt = mode_get() == MODE_BT;
    const char *label = bt ? "BLUETOOTH" : "PC";
    int tw = (int)strlen(label) * 7, w = 8 + 10 + 4 + tw + 5 + 5 + 8, x = 120 - w / 2, y = 16;
    pill(x, y, w, 18, bt ? C_BT_TINT : C_PC_TINT);
    (bt ? icon_bt : icon_monitor)(x + 13.f, y + 9.f, 10.f, accent());
    gfx_text(x + 22, y + 3, label, &Font12, accent());
    gfx_disc(x + w - 10.5f, y + 9.f, 2.6f, out_ready() ? C_OK : C_BAD);
}

static void dots(int count, int active) {
    for (int i = 0; i < count; i++)
        gfx_disc(120.f + (i * 2 - (count - 1)) * 6.f, 229.f, 2.6f, i == active ? C_TEXT : C_DOT_OFF);
}

static void draw_clip() {
    enum { CX0 = 32, CY0 = 58, CW = 176, CH = 86, COLS = 22, ROWS = 6 };
    char info[40], buf[40];
    text_c(38, "Clipboard", &Font16, C_TEXT);

    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    int len = app.clip_len;
    gfx_rrect(CX0, CY0, CW, CH, 12.f, len ? C_SURF : C_INNER);
    if (len == 0) {
        text_c(CY0 + 28, "Select text on PC,", &Font12, C_DIM);
        text_c(CY0 + 44, "then tap Copy", &Font12, C_DIM);
    } else {
        int row = 0, col = 0;
        for (int i = 0; i < len && row < ROWS; i++) {
            char c = app.clip[i];
            if (c == '\r') continue;
            if (c == '\n') { row++; col = 0; continue; }
            if (c == '\t') c = ' ';
            if (col == COLS) { row++; col = 0; if (row >= ROWS) break; }
            gfx_char(CX0 + 8 + col * 7, CY0 + 7 + row * 12, c, &Font12, C_CLIP);
            col++;
        }
    }
    snprintf(info, sizeof info, len ? "%d chars from %s" : "Empty", len, app.clip_src);
    xSemaphoreGive(clip_mtx);

    bool pasting = app.clip_state == CLIP_PASTING;
    if (pasting && len) {
        gfx_rrect(48, 152, 144, 4, 2.f, C_SURF2);
        gfx_rrect(48, 152, 144 * app.paste_pos / len + 1, 4, 2.f, accent());
    } else {
        text_c(149, msg_or(info, buf, sizeof buf), &Font12, C_DIM);
    }

    button(BTN_COPY_X, BTN_Y, BTN_W, BTN_H, C_SURF2, C_TEXT,
           app.clip_state == CLIP_COPYING ? "..." : "Copy", &Font16, icon_copy, nullptr);
    if (pasting) button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, C_BAD, C_BG, "Stop", &Font16, nullptr, nullptr);
    else button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, len ? accent() : C_SURF2, len ? C_BG : C_FAINT,
                "Paste", &Font16, nullptr, icon_arrow_right);
}

static void draw_jig() {
    char s[40], buf[40];
    bool on = app.jig_on;
    text_c(38, "Jiggler", &Font16, C_TEXT);
    gfx_ring(JIG_CX, JIG_CY, JIG_R, 2.f, on ? accent() : C_SURF2);
    gfx_disc(JIG_CX, JIG_CY, 44.f, C_INNER);
    if (on)
        gfx_disc(JIG_CX + cosf(app.jig_angle) * 50.f, JIG_CY + sinf(app.jig_angle) * 50.f, 6.f, accent());
    text_c(JIG_CY - 18, on ? "ON" : "OFF", &Font24, on ? accent() : C_DIM);
    text_c(JIG_CY + 7, on ? "TAP TO STOP" : "TAP TO START", &Font12, C_DIM);

    const char *status = "Tap to start";
    uint16_t scol = C_TEXT;
    if (on && !out_ready()) { status = out_down_reason(); scol = C_BAD_TXT; }
    else if (on && app.jig_paused) status = "Paused: pasting";
    else if (on && app.jig_phase == JIG_CIRCLE) {
        int secs = (int)((int32_t)(app.jig_next_menu_ms - now_ms()) / 1000);
        snprintf(s, sizeof s, "Next menu in %ds", secs < 0 ? 0 : secs);
        status = s;
    } else if (on && (app.jig_phase == JIG_CLICK_DOWN || app.jig_phase == JIG_MENU_OPEN)) status = "Right-click menu";
    else if (on && app.jig_phase == JIG_ESC_DOWN) status = "Esc";
    else if (on) status = "Pausing";
    text_c(184, status, &Font12, scol);

    char stats[40];
    if (on) {
        uint32_t up = (now_ms() - app.jig_started_ms) / 1000;
        snprintf(stats, sizeof stats, "%lu menus %02lu:%02lu:%02lu", (unsigned long)app.jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
    } else {
        strcpy(stats, "Menu every 45-150s");
    }
    text_c(200, msg_or(stats, buf, sizeof buf), &Font12, C_DIM);
}

static const char *bt_row_status(uint16_t *col) {
    *col = C_DIM;
    switch (ble_state()) {
    case BT_CONNECTED: *col = C_OK; return "Connected";
    case BT_PAIRING: return "Pairing...";
    case BT_WAITING: return "Paired";
    case BT_OFF: return "Paired, off";
    default: return "Not paired";
    }
}

static void draw_settings() {
    char buf[40];
    icon_cog(73.f, 46.f, 15.f, C_DIM);
    gfx_text(85, 38, "Settings", &Font16, C_TEXT);
    text_c(60, "OUTPUT", &Font12, C_DIM);

    bool avail = mode_bt_available(), bt = mode_get() == MODE_BT;
    pill(38, 74, 164, 40, C_SURF);
    button(SEG_BT_X, SEG_Y, SEG_W, SEG_H, bt ? C_BT : C_SURF, bt ? C_BG : (avail ? C_DIM : C_FAINT),
           "Bluetooth", &Font12, avail ? icon_bt : icon_lock, nullptr);
    button(SEG_PC_X, SEG_Y, SEG_W, SEG_H, bt ? C_SURF : C_PC, bt ? C_DIM : C_BG,
           "PC", &Font12, icon_monitor, nullptr);
    const char *cap = !avail ? "Pair Bluetooth to switch" : (bt ? "Sends to Bluetooth host" : "Sends to this PC");
    text_c(120, msg_or(cap, buf, sizeof buf), &Font12, C_DIM);

    uint16_t scol;
    const char *st = bt_row_status(&scol);
    gfx_rrect(ROW_X, ROW_Y, ROW_W, ROW_H, 14.f, C_SURF);
    gfx_disc(55.f, 162.f, 13.f, RGB(28, 42, 67));
    icon_bt(55.f, 162.f, 13.f, C_BT);
    gfx_text(75, 147, "Bluetooth", &Font12, C_TEXT);
    gfx_text(75, 163, st, &Font12, scol);
    icon_chevron_right(193.f, 162.f, 11.f, C_DIM);
}

static void draw_bt() {
    char s[40], buf[40];
    bt_state_t st = ble_state();
    gfx_disc(BACK_CX, BACK_CY, 14.f, C_SURF);
    icon_chevron_left(BACK_CX - 1.f, BACK_CY, 12.f, C_TEXT);
    text_c(41, "Bluetooth", &Font16, C_TEXT);

    if (st == BT_UNPAIRED) {
        gfx_disc(120.f, 94.f, 26.f, C_BT_TINT);
        icon_bt(120.f, 94.f, 22.f, C_BT);
        text_c(126, "Not paired", &Font16, C_TEXT);
        text_c(146, "PC: Settings>Bluetooth", &Font12, C_DIM);
        text_c(160, msg_or("Add device>Touch Deck", buf, sizeof buf), &Font12, C_DIM);
        button(BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H, C_BT, C_BG, "Pair", &Font16, nullptr, nullptr);
    } else if (st == BT_PAIRING) {
        text_c(66, "Enter this PIN on PC", &Font12, C_DIM);
        snprintf(s, sizeof s, "%06lu", (unsigned long)ble_passkey());
        for (int i = 0; i < 6; i++) {
            int bx = 47 + i * 24 + (i >= 3 ? 5 : 0);
            gfx_rrect(bx, 84, 21, 30, 6.f, C_SURF);
            gfx_char(bx + 2, 87, s[i], &Font24, C_TEXT);
        }
        text_c(124, msg_or("Pick Touch Deck", buf, sizeof buf), &Font12, C_DIM);
        int left = ble_pair_secs_left();
        snprintf(s, sizeof s, "Expires in %d:%02d", left / 60, left % 60);
        text_c(140, s, &Font12, C_BT);
        button(BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H, C_SURF2, C_TEXT, "Cancel", &Font16, nullptr, nullptr);
    } else {
        bool conn = st == BT_CONNECTED, off = st == BT_OFF;
        uint16_t dot = conn ? C_OK : C_DIM;
        gfx_disc(120.f, 92.f, 25.f, C_SURF);
        icon_monitor(120.f, 91.f, 22.f, C_TEXT);
        gfx_disc(139.f, 110.f, 6.5f, C_BG);
        gfx_disc(139.f, 110.f, 4.5f, dot);
        text_c(122, conn ? "CONNECTED" : (off ? "DISCONNECTED" : "WAITING"), &Font12, dot);
        const char *host = ble_host_name();
        snprintf(s, sizeof s, "%.18s", host[0] ? host : "Paired PC");
        text_c(138, s, &Font16, C_TEXT);
        const char *sub = conn ? (ble_ready() ? "Keyboard + mouse" : "Connecting...")
                        : off ? "Tap Connect to resume" : "PC will reconnect";
        text_c(158, msg_or(sub, buf, sizeof buf), &Font12, C_DIM);
        if (off) button(BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_OK, C_BG, "Connect", &Font12, nullptr, nullptr);
        else button(BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_SURF2, C_TEXT, "Disconnect", &Font12, nullptr, nullptr);
        pill(BT_BTN_R_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_BAD);
        pill(BT_BTN_R_X + 1, BT_BTN2_Y + 1, BT_BTN2_W - 2, BT_BTN2_H - 2, C_BG);
        gfx_text_centered(BT_BTN_R_X + BT_BTN2_W / 2, BT_BTN2_Y + 11, "Forget", &Font12, C_BAD_TXT);
    }
}

void ui_task(void *) {
    uint32_t drawn_seq = ~0u, last_frame = 0;
    bool first = true;
    for (;;) {
        int screen = app.screen;
        bool in_bt = app.in_bt;
        uint32_t seq = app.redraw_seq;
        uint32_t period = (screen == SCR_JIG && app.jig_on) || app.clip_state != CLIP_IDLE ? 100 : 500;
        if (seq == drawn_seq && now_ms() - last_frame < period) {
            vTaskDelay(pdMS_TO_TICKS(5));
            continue;
        }
        drawn_seq = seq;
        last_frame = now_ms();

        frame();
        if (in_bt) { draw_bt(); dots(1, 0); }
        else {
            if (screen == SCR_CLIP) draw_clip();
            else if (screen == SCR_JIG) draw_jig();
            else draw_settings();
            dots(SCR_COUNT, screen);
        }
        display_push();
        display_wait();
        app.frames++;
        if (first) { display_backlight(200); first = false; }
    }
}
```

- [ ] **Step 6: Touch handling to the new geometry (`main.cpp`).** Replace the Settings branch and `on_touch_bt` with:

```cpp
static bool near(const touch_event_t &e, int cx, int cy, int r) {
    int dx = e.x - cx, dy = e.y - cy;
    return dx * dx + dy * dy <= r * r;
}

static void on_touch_bt(const touch_event_t &e) {
    if (e.type == EV_SWIPE_R || (e.type == EV_TAP && near(e, BACK_CX, BACK_CY, BACK_HIT_R))) {
        app.in_bt = false;
        app_redraw();
        return;
    }
    if (e.type != EV_TAP) return;
    bt_state_t st = ble_state();
    if (st == BT_UNPAIRED || st == BT_PAIRING) {
        if (in_rect(e, BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H)) {
            if (st == BT_UNPAIRED) ble_pair_start();
            else ble_pair_cancel();
        }
    } else if (in_rect(e, BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H)) {
        if (st == BT_OFF) ble_reconnect();
        else ble_disconnect();
    } else if (in_rect(e, BT_BTN_R_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H)) {
        ble_forget();
        mode_set(MODE_PC);
    }
    app_redraw();
}
```

and in `on_touch`:

```cpp
    } else if (app.screen == SCR_SETTINGS) {
        if (in_rect(e, SEG_BT_X, SEG_Y, SEG_W, SEG_H)) {
            if (!mode_set(MODE_BT)) app_message("Pair Bluetooth first");
        } else if (in_rect(e, SEG_PC_X, SEG_Y, SEG_W, SEG_H)) {
            mode_set(MODE_PC);
        } else if (in_rect(e, ROW_X, ROW_Y, ROW_W, ROW_H)) {
            app.in_bt = true;
            app_redraw();
        }
    }
```

(The jiggler tap uses `JIG_CX/JIG_CY/JIG_R + 10`; Copy/Paste use the new `BTN_*`.)

- [ ] **Step 7: ASCII host names (`ble_hid.cpp`).** Add, and use it in `on_name` in place of the raw copy:

```cpp
// PC/phone names arrive as UTF-8 ("Nik’s iPhone"); the fonts are ASCII only.
static void fold_ascii(const uint8_t *in, int n, char *out, int cap) {
    int o = 0;
    for (int i = 0; i < n && o < cap - 1;) {
        uint8_t c = in[i];
        if (c < 0x80) { out[o++] = (c >= 32 && c < 127) ? (char)c : '?'; i++; continue; }
        int len = (c >= 0xF0) ? 4 : (c >= 0xE0) ? 3 : (c >= 0xC0) ? 2 : 1;
        uint32_t cp = 0;
        if (len == 3 && i + 2 < n) cp = ((c & 0x0F) << 12) | ((in[i + 1] & 0x3F) << 6) | (in[i + 2] & 0x3F);
        out[o++] = (cp == 0x2018 || cp == 0x2019) ? '\'' : (cp == 0x201C || cp == 0x201D) ? '"' : '?';
        i += len;
    }
    out[o] = 0;
}
```

In `on_name`: copy the mbuf into a local `uint8_t raw[64]` (up to 63 bytes), then `fold_ascii(raw, n, host, sizeof host)`. In `ble_init`, fold the saved `host` once after loading it (it may hold a name saved before this change): `fold_ascii((const uint8_t *)host, strlen(host), host, sizeof host)`. The fold is safe in place because output never gets longer than input.

- [ ] **Step 8: Build, flash, run all tests**

Run: `cd esp32c3 && python -m platformio run -t upload --upload-port COM7`, then `python -m pytest tools/tests -v`
Expected: all pass (the unpaired-only checks skip when a bond exists).

- [ ] **Step 9: User visual check.** Ask the user to look at all four screens against the mockup. Colours, ring, chip and buttons must fit inside the circle with nothing clipped.

- [ ] **Step 10: Checkpoint.** In `CLAUDE.md` ESP32-C3 section: the UI source of truth is the mockup link, `icons.c` is shared, and screen geometry lives in `ui.h`.

---

### Task 5: RP2040 restyle (same design language, USB-only)

**Files:**
- Create: `src/icons.h`, `src/icons.c` (copies of the ESP32-C3 files)
- Modify: `src/gfx.h/.c` (add `gfx_rrect_ring`), `src/ui.h` (geometry), `src/ui.c` (frame, chip, Clipboard, Jiggler), `src/app.h` (add `usb_mounted`), `src/main.c` (set `usb_mounted`), `CMakeLists.txt` (add `src/icons.c`)
- Test: build + `python tools/flash.py` + the helper handshake (`HELLO`→`PONG`, `DBG` liveness) + user visual check

**Interfaces:**
- Consumes: `icons.h` (Task 4).
- Produces: `void gfx_rrect_ring(int x, int y, int w, int h, float r, float width, uint16_t col);`

- [ ] **Step 1: `gfx_rrect_ring` in `src/gfx.c`** (only touches pixels near the edge, so it's cheap on a full-screen frame)

```c
void gfx_rrect_ring(int x, int y, int w, int h, float rad, float width, uint16_t color) {
    float hx = w * 0.5f, hy = h * 0.5f, cx = x + hx, cy = y + hy;
    int band = (int)(rad + width + 2);
    for (int py = y; py < y + h; py++) {
        bool edge_row = py < y + band || py >= y + h - band;
        for (int px = x; px < x + w; px++) {
            if (!edge_row && px >= x + (int)width + 2 && px < x + w - (int)width - 2) {
                px = x + w - (int)width - 3;          // skip the untouched middle
                continue;
            }
            float qx = fabsf(px + 0.5f - cx) - (hx - rad), qy = fabsf(py + 0.5f - cy) - (hy - rad);
            float d = (qx > 0.f && qy > 0.f) ? sqrtf(qx * qx + qy * qy) - rad : fmaxf(qx, qy) - rad;
            float a = fminf(0.5f - d, d + width + 0.5f);   // inside the outer edge and outside the inner edge
            plot(px, py, color, a > 1.f ? 1.f : a);
        }
    }
}
```
Declare it in `gfx.h`.

- [ ] **Step 2: Chip + ring on every RP2040 screen.** In `ui.c`, add the palette defines from Task 4 Step 5 (`C_BG` … `C_PC_TINT`), `#include "icons.h"`, and:

```c
// USB is the only output here, so the accent is always amber (PC).
static void frame_usb(void) {
    gfx_rrect_ring(0, 0, LCD_W, LCD_H, 38.f, 3.f, C_PC);
    const char *label = "USB";
    int tw = 3 * 7, w = 8 + 10 + 4 + tw + 5 + 5 + 8, x = 120 - w / 2, y = 5;
    gfx_rrect(x, y, w, 18, 9.f, C_PC_TINT);
    icon_monitor(x + 13.f, y + 9.f, 10.f, C_PC);
    gfx_text(x + 22, y + 3, label, &Font12, C_PC);
    gfx_disc(x + w - 10.5f, y + 9.f, 2.6f, app.usb_mounted ? C_OK : C_BAD);
}
```

Call it in `ui_core1_main` right after `gfx_fill(COL_BG)` for every screen (the watch dial starts at y=24, so the chip at y=5..23 sits above it). Change `COL_BG` to `C_BG`. In `main.c`'s loop, add `app.usb_mounted = tud_mounted();` (`app.h`: `volatile bool usb_mounted;`).

- [ ] **Step 3: Clipboard and Jiggler to the new look (240×280, content shifted down 20 px from the round layout).** `ui.h` geometry:

```c
#define BTN_Y      204
#define BTN_H      44
#define BTN_W      98
#define BTN_COPY_X 20
#define BTN_PASTE_X 122
#define JIG_CX 120
#define JIG_CY 140
#define JIG_R  62
```
Then replace `draw_clip` and `draw_jig` in `ui.c` with the Task 4 versions adapted as follows (write them out fully in `ui.c`; the RP2040 is C, so use `NULL` for `nullptr`, `mutex_enter_blocking(&clip_mtx)`/`mutex_exit` for the semaphore, and the fixed amber `C_PC` for `accent()`):
- Clipboard: title y 40; card `(20, 64, 200, 110)`, 26 cols × 8 rows starting `(28, 71)`; info/progress at y 182 (progress bar `(40, 186, 160, 4)`); Copy/Paste buttons from the new `BTN_*`, labels in Font16.
- Jiggler: title y 40; ring `JIG_R`, inner disc 49, dot radius 55; ON/OFF at `JIG_CY - 18`; hint at `JIG_CY + 7`; status y 214; stats y 232. Down reason: `"Plug into USB"` when `!app.usb_mounted`.
- Keep `page_dots` at y 268 and the watch face as is, apart from the frame.

`main.c`'s touch handlers already read `BTN_*` and `JIG_*`, so no change there.

- [ ] **Step 4: Build, flash, verify**

Run: `P=~/.pico-sdk; export PATH="$P/ninja/v1.12.1:$P/cmake/v3.31.5/bin:$PATH"; ninja -C build && python tools/flash.py`, then a `HELLO`/`DBG` probe (`frames` increasing, touch `fails=0`).
Expected: `PONG`, frames increasing. The user confirms the look on the device.

- [ ] **Step 5: Checkpoint.** In `CLAUDE.md`: the RP2040 uses the same visual language (fixed amber USB chip), and `icons.c` is duplicated in both trees and must be kept in sync.
