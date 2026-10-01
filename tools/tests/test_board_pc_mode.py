"""Drives the ESP32-C3 over USB serial, acting as the helper, and checks that
PC output mode emits K/M lines. Nothing is performed on the PC."""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import serial
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
        return next(l for l in self.take() if l.startswith("LOG up="))

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
    try:
        b = Board()
    except serial.SerialException as e:
        pytest.skip(f"{PORT} is busy (quit the Touch Deck app): {e}")
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
    board.take(); board.send("TAP 160 160"); board.pump(1.0)       # Paste (buttons at y 140..180)
    keys = [l for l in board.take() if l.startswith("K ")]
    assert keys == ["K 02 0B", "K 00 00", "K 00 0C", "K 00 00", "K 00 28", "K 00 00"]

def test_bluetooth_mode_refused_while_unpaired(board):
    d = board.dbg()
    if " state=0 " not in d:
        pytest.skip("board has a Bluetooth bond; Forget it to run this check")
    board.send("MODE BT"); board.pump(0.3)
    assert board.field("mode") == "0"

SCALE_PILL = "TAP 32 120"                               # left of the letter

def test_scale_pill_cycles_jiggler_scale(board):
    board.goto(1)
    order = ["1.0", "1.5", "2.0"]
    seen = [board.field("jscale")]
    for _ in range(3):
        board.send(SCALE_PILL); board.pump(0.2)
        seen.append(board.field("jscale"))
    i = order.index(seen[0])
    assert seen == [order[(i + k) % 3] for k in range(4)]

def _mean_step(board, secs):
    board.take(); board.pump(secs)
    steps = [l.split() for l in board.take() if l.startswith("M 00 ")]
    return sum(abs(int(dx)) + abs(int(dy)) for _, _, dx, dy in steps) / max(1, len(steps))

def test_jiggler_scale_widens_the_movement(board):
    board.goto(1)
    while board.field("jscale") != "1.0":
        board.send(SCALE_PILL); board.pump(0.2)
    board.send("TAP 120 115"); board.pump(1.5)          # jiggler on at 1x, let it settle
    small = _mean_step(board, 2.0)
    board.send(SCALE_PILL); board.send(SCALE_PILL); board.pump(1.5)   # -> 2x, allow the easing
    large = _mean_step(board, 2.0)
    board.send(SCALE_PILL); board.pump(0.2)             # back to 1x
    assert large > 1.6 * small

def test_ver_reports_board_and_firmware_version(board):
    board.take(); board.send("VER"); board.pump(0.4)
    replies = [l for l in board.take() if l.startswith("VERSION ")]
    assert replies and replies[-1].split()[1] == "esp32c3-128"


# ---------- app mirror protocol (firmware 1.6.0) ----------

def state_lines(board, secs):
    board.take(); board.pump(secs)
    return [l for l in board.take() if l.startswith("STATE ")]

def fields(line):
    return dict(kv.split("=", 1) for kv in line.split()[1:])

def latest_state(board):
    """Forces a fresh report (WATCH 1 re-sends once) and returns its fields."""
    board.send("WATCH 1")
    return fields(state_lines(board, 0.3)[-1])

def test_watch_streams_state(board):
    board.send("WATCH 1")
    lines = state_lines(board, 0.5)
    assert lines, "no STATE after WATCH 1"
    f = fields(lines[-1])
    assert set(f) >= {"jig", "letter", "scale", "phase", "x", "y", "clip", "paste"}
    assert f["letter"] in "OWMNZXCVHJLBGD"
    board.send("WATCH 0"); board.pump(0.2)
    assert not state_lines(board, 0.6), "STATE kept coming after WATCH 0"

def test_anim_demo_moves_the_dot_in_state(board):
    board.send("WATCH 1"); board.send("ANIM 1")
    try:
        pts = {(fields(l)["x"], fields(l)["y"]) for l in state_lines(board, 1.2)}
        assert len(pts) >= 5, "dot position did not stream"
    finally:
        board.send("ANIM 0"); board.send("WATCH 0"); board.pump(0.2)

def test_jig_scale_command(board):
    start = int(latest_state(board)["scale"])
    try:
        for want in (2, 0, 1):
            board.send(f"JIG SCALE {want}"); board.pump(0.2)
            assert board.field("jscale") == ["1.0", "1.5", "2.0"][want]
    finally:
        board.send(f"JIG SCALE {start}"); board.send("WATCH 0"); board.pump(0.2)

def test_jig_on_off_command(board):
    board.send("JIG ON"); board.pump(0.3)
    assert board.field("jig") == "1"
    board.send("JIG OFF"); board.pump(0.2)
    assert board.field("jig") == "0"

def test_clip_clear_ignored_while_empty(board):
    board.s.write(b"CLIP 5 test" + bytes([10]) + b"hello"); board.pump(0.3)
    assert latest_state(board)["clip"] == "5"
    board.send("CLIP CLEAR"); board.pump(0.3)
    assert latest_state(board)["clip"] == "0"
    board.send("CLIP CLEAR"); board.pump(0.3)
    assert latest_state(board)["clip"] == "0"
    board.send("WATCH 0"); board.pump(0.2)

def test_trash_tap_clears_the_clip(board):
    board.goto(0)
    board.s.write(b"CLIP 5 test" + bytes([10]) + b"hello"); board.pump(0.3)
    assert board.field("clip") == "5"
    board.send("TAP 178 42"); board.pump(0.3)
    assert board.field("clip") == "0"

def test_jig_off_mid_menu_releases_the_button(board):
    """JIG OFF right after the right-button press must still release it (M 00)."""
    board.send("JIG ON"); board.pump(0.5)
    board.take(); board.send("JIG MENU")
    end = time.time() + 2.0
    while time.time() < end and not any(l.startswith("M 02") for l in board.lines):
        board.pump(0.01)
    assert any(l.startswith("M 02") for l in board.lines), "menu never pressed the right button"
    board.send("JIG OFF"); board.pump(0.6)
    after = board.take()
    down = max(i for i, l in enumerate(after) if l.startswith("M 02"))
    assert any(l.startswith("M 00") for l in after[down + 1:]), "right button left held"


def jig_cfg(board):
    return tuple(board.field(k) for k in ("jmenu", "jkey", "jopen", "jpause"))


def test_jig_cfg_round_trips_and_rejects_bad_values(board):
    """Jiggler settings (firmware 1.8.0): set, read back in DBG and STATE, bad values ignored."""
    start = jig_cfg(board)
    try:
        board.send("JIG CFG 0 1 7 3"); board.pump(0.3)
        assert jig_cfg(board) == ("0", "1", "7", "3")
        board.send("WATCH 1"); board.pump(0.4)
        st = [l for l in board.take() if l.startswith("STATE ")]
        assert st and all(f in st[-1].split() for f in ("jmenu=0", "jkey=1", "jopen=7", "jpause=3"))
        board.send("WATCH 0")
        for bad in ("JIG CFG 1 0 99 0", "JIG CFG 2 0 2 0", "JIG CFG 1 0 2", "JIG CFG 1 0 2 0 9", "JIG CFG x"):
            board.send(bad); board.pump(0.2)
        assert jig_cfg(board) == ("0", "1", "7", "3")
    finally:
        board.send("JIG CFG " + " ".join(start)); board.pump(0.3)


def test_jiggler_settings_page_taps(board):
    """Each control on the Jiggler settings page (firmware 1.8.0) changes its setting; Menu open
    ignores taps while the context menu is off."""
    start = tuple(board.field(k) for k in ("jmenu", "jkey", "jopen", "jpause"))
    try:
        board.send("JIG CFG 1 0 2 0"); board.pump(0.3)
        board.goto(1)
        assert board.field("screen") == "1" and board.field("jset") == "0"
        board.send("TAP 40 188"); board.pump(0.25)            # the cog opens the panel
        assert board.field("jset") == "1"
        def tap(xy):
            board.send("TAP %d %d" % xy); board.pump(0.25)
        def cfg():
            return tuple(board.field(k) for k in ("jmenu", "jkey", "jopen", "jpause"))
        tap((195, 140)); tap((195, 140))
        assert cfg() == ("1", "0", "4", "0")
        tap((139, 140))
        assert cfg() == ("1", "0", "3", "0")
        tap((195, 172))
        assert cfg() == ("1", "0", "3", "1")
        tap((188, 108)); assert cfg()[1] == "1"
        tap((146, 108)); assert cfg()[1] == "0"
        tap((184, 76)); assert cfg()[0] == "0"
        tap((195, 140))                                   # dimmed: no change
        assert cfg() == ("0", "0", "3", "1")
        tap((139, 172)); tap((139, 172))             # stops at 0
        assert cfg()[3] == "0"
        board.send("TAP 50 48"); board.pump(0.25)        # X closes it
        assert board.field("jset") == "0" and board.field("screen") == "1"
        board.send("TAP 40 188"); board.pump(0.25)
        board.send("SWIPE R"); board.pump(0.25)                        # so does a right swipe
        assert board.field("jset") == "0" and board.field("screen") == "1"
    finally:
        board.send("JIG CFG " + " ".join(start)); board.pump(0.3)


def test_a_settings_change_is_streamed_without_asking(board):
    """While watching, JIG CFG alone must produce a STATE with the new values (the app's Settings
    section follows the board): not just after a forced WATCH 1 resend."""
    start = tuple(board.field(k) for k in ("jmenu", "jkey", "jopen", "jpause"))
    try:
        board.send("WATCH 1"); board.pump(0.5); board.take()
        board.send("JIG CFG 1 1 9 4"); board.pump(0.5)
        st = [l for l in board.take() if l.startswith("STATE ")]
        assert st and all(f in st[-1].split() for f in ("jkey=1", "jopen=9", "jpause=4")), st[-1:] or "no STATE"
    finally:
        board.send("WATCH 0")
        board.send("JIG CFG " + " ".join(start)); board.pump(0.3)
