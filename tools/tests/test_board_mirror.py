"""The device mirror against the real RP2040 (firmware 1.7.0+): what the board
streams after WATCH 1, drawn by the host-compiled pages (hostui/), must be the
board's own framebuffer, pixel for pixel (FBCRC). Nothing is typed or clicked
on the PC; the jiggler only animates (ANIM 1)."""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import serial
import tdui_host
from touchdeck import find_board
from test_board_pc_mode import Board

RP_PORT = find_board("rp2040-169")   # not the round RP2350, also CAFE:4011
pytestmark = pytest.mark.skipif(RP_PORT is None, reason="RP2040 Touch Deck not connected")


@pytest.fixture(scope="module")
def renderer(tmp_path_factory):
    r = tdui_host.build(str(tmp_path_factory.mktemp("tdui")))
    if r is None:
        pytest.skip(tdui_host.jig_host.NO_COMPILER)
    return r["rp2040"]


class Mirror:
    """A board plus the mirror state built from its lines, as the app keeps it."""

    def __init__(self, board, renderer):
        self.b, self.r = board, renderer
        self.st = tdui_host.UiState()
        self.states = []

    def pump(self, secs):
        self.b.pump(secs)
        for line in self.b.take():
            if line.startswith(("STATE ", "TEXT ", "CLIPTEXT")) or line in ("TEXT msg", "CLIPTEXT"):
                self.r.apply(self.st, line)
                if line.startswith("STATE "):
                    self.states.append(line)

    def fbcrc(self, x=0, y=0, w=240, h=280):
        self.b.send(f"FBCRC {x} {y} {w} {h}")
        end = time.time() + 2
        while time.time() < end:
            self.b.pump(0.05)
            for line in self.b.lines:
                if line.startswith("LOG fbcrc "):
                    self.b.lines.remove(line)
                    return int(line.split()[2], 16)
        raise AssertionError("no FBCRC reply")


@pytest.fixture
def mirror(renderer):
    try:
        b = Board(RP_PORT)
    except serial.SerialException as e:
        pytest.skip(f"{RP_PORT} is busy (quit the Touch Deck app): {e}")
    b.take(); b.send("VER"); b.pump(0.4)
    ver = [l for l in b.take() if l.startswith("VERSION ")]
    if not ver or tuple(int(p) for p in ver[-1].split()[2].split(".")) < (1, 7, 0):
        b.s.close()
        pytest.skip("firmware older than 1.7.0")
    m = Mirror(b, renderer)
    b.send("WATCH 1")
    m.pump(0.4)
    try:
        yield m
    finally:
        b.send("ANIM 0"); b.send("CLIP CLEAR"); b.goto(0); b.send("WATCH 0"); b.pump(0.3)
        b.s.close()


def put_clip(b, text):
    b.send(f"CLIP {len(text)} test"); b.s.write(text.encode())


def settle(m, page, secs=3.0):
    m.b.goto(page)
    m.pump(secs)                                   # messages expire after 2.5 s
    assert m.st.screen == page


def test_clipboard_page_is_pixel_identical(mirror):
    put_clip(mirror.b, "Mirror check\r\nwraps a long line of text across the box, then\n\ttabs")
    settle(mirror, 1)
    assert mirror.st.clip_len > 40 and mirror.st.clip_src == b"test"
    frame = mirror.r.render(mirror.st)
    assert mirror.fbcrc() == mirror.r.crc(frame)


def test_empty_clipboard_and_message(mirror):
    mirror.b.send("CLIP CLEAR")
    settle(mirror, 1)
    assert mirror.fbcrc() == mirror.r.crc(mirror.r.render(mirror.st))


def test_jiggler_page_is_pixel_identical_for_each_scale(mirror):
    settle(mirror, 2)
    if mirror.st.jig_on:
        pytest.skip("the jiggler is on (its dot and countdown move); not switching it off")
    start = mirror.st.jig_scale
    try:
        for i in range(3):
            mirror.b.send("BTN")                   # BOOT on the jiggler page: next scale
            mirror.pump(0.8)
            frame = mirror.r.render(mirror.st)
            assert mirror.fbcrc() == mirror.r.crc(frame), f"scale {mirror.st.jig_scale}"
    finally:
        while mirror.st.jig_scale != start:
            mirror.b.send("BTN"); mirror.pump(0.3)


def test_anim_streams_the_dot_at_about_20_hz(mirror):
    settle(mirror, 2, 1.0)
    mirror.states.clear()
    mirror.b.send("ANIM 1")
    mirror.pump(2.0)
    mirror.b.send("ANIM 0")
    dots = [(l.split(" x=")[1].split()[0], l.split(" y=")[1].split()[0]) for l in mirror.states]
    assert len(set(dots)) >= 25, len(set(dots))    # 20 Hz for 2 s, minus start-up
    assert mirror.st.jig_demo == 1 or "demo=1" in "".join(mirror.states)


def test_watch_page_is_pixel_identical(mirror):
    """Hands, ticks and all: the board's sinf/cosf and the PC's agree to the pixel. Right after
    showing a second the board draws the next one ahead into its framebuffer, so fb holds
    either the second on screen (t) or t+1."""
    settle(mirror, 0, 1.5)
    for _ in range(3):
        t0 = mirror.st.time_s
        end = time.time() + 2
        while mirror.st.time_s == t0 and time.time() < end:   # the next second's STATE
            mirror.pump(0.02)
        mirror.pump(0.35)                                    # its drawn-ahead t+1 frame is finished by now
        got, t = mirror.fbcrc(), mirror.st.time_s
        want = []
        for dt in (0, 1):
            st = tdui_host.UiState.from_buffer_copy(mirror.st)
            st.time_s = (t + dt) % 86400
            want.append(mirror.r.crc(mirror.r.render(st)))
        assert got in want, (t, hex(got), [hex(w) for w in want])


def test_jiggler_settings_page_is_pixel_identical(mirror):
    """The Jiggler settings page (firmware 1.8.0), with the menu on and off (dimmed row)."""
    start = None
    try:
        settle(mirror, 2, 1.0)
        mirror.b.send("TAP 30 230"); mirror.pump(0.6)      # open the panel
        assert mirror.st.sub == 2
        start = (mirror.st.jig_menu_on, mirror.st.jig_key, mirror.st.jig_open_s, mirror.st.jig_pause_s)
        for cfg in ("1 0 2 0", "0 1 17 9"):
            mirror.b.send("JIG CFG " + cfg)
            mirror.pump(0.8)
            assert mirror.fbcrc() == mirror.r.crc(mirror.r.render(mirror.st)), cfg
    finally:
        if start:
            mirror.b.send("JIG CFG %d %d %d %d" % start); mirror.pump(0.3)
        mirror.b.send("SWIPE R"); mirror.pump(0.3)                      # close it
