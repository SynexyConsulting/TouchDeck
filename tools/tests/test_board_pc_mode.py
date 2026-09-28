"""Drives the ESP32-C3 over USB serial, acting as the helper, and checks that
PC output mode emits K/M lines. Nothing is performed on the PC."""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
from touchdeck import ESP_PID, ESP_VID, find_port, open_serial

PORT = find_port(ESP_VID, ESP_PID)
pytestmark = pytest.mark.skipif(PORT is None, reason="ESP32-C3 Touch Deck not connected")

class Board:
    def __init__(self, port=None):
        self.s = open_serial(port or PORT, timeout=0.02)
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

def jiggler_off(b):
    if b.field("jig") == "1":
        b.goto(1); b.send("TAP 120 115"); b.pump(0.3)

@pytest.fixture
def board():
    b = Board()
    try:                                  # always release COM7, even when setup fails
        b.send("MODE PC"); b.pump(0.2)
        jiggler_off(b)                    # start from a known state
        yield b
        jiggler_off(b)
    finally:
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

@pytest.mark.skip(reason="ESP32-C3 BOOT button not wired into the firmware yet (button.cpp is WIP)")
def test_boot_button_cycles_jiggler_scale(board):
    board.goto(1)
    order = ["1.0", "1.5", "2.0"]
    seen = [board.field("jscale")]
    for _ in range(3):
        board.send("BTN"); board.pump(0.2)
        seen.append(board.field("jscale"))
    i = order.index(seen[0])
    assert seen == [order[(i + k) % 3] for k in range(4)]

def _mean_step(board, secs):
    board.take(); board.pump(secs)
    steps = [l.split() for l in board.take() if l.startswith("M 00 ")]
    return sum(abs(int(dx)) + abs(int(dy)) for _, _, dx, dy in steps) / max(1, len(steps))

@pytest.mark.skip(reason="ESP32-C3 BOOT button not wired into the firmware yet (button.cpp is WIP)")
def test_jiggler_scale_widens_the_movement(board):
    board.goto(1)
    while board.field("jscale") != "1.0":
        board.send("BTN"); board.pump(0.2)
    board.send("TAP 120 115"); board.pump(1.5)          # jiggler on at 1x, let it settle
    small = _mean_step(board, 2.0)
    board.send("BTN"); board.send("BTN"); board.pump(1.5)   # -> 2x, allow the easing
    large = _mean_step(board, 2.0)
    board.send("BTN"); board.pump(0.2)                  # back to 1x
    assert large > 1.6 * small
