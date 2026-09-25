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
