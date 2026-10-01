"""The device mirror against the round RP2350 (rp2350-128): the host-compiled round
pages (hostui tdui_rp2350) must draw exactly the board's framebuffer (FBCRC).
Nothing is typed or clicked on the PC; the jiggler only animates (ANIM 1)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import serial
import tdui_host
from touchdeck import find_board
from test_board_mirror import Mirror, put_clip
from test_board_pc_mode import Board

PORT = find_board("rp2350-128")
pytestmark = pytest.mark.skipif(PORT is None, reason="RP2350 round Touch Deck not connected")
WATCH, CLIP, JIG = 0, 1, 2


@pytest.fixture(scope="module")
def renderer(tmp_path_factory):
    r = tdui_host.build(str(tmp_path_factory.mktemp("tdui")))
    if r is None:
        pytest.skip("MSVC not installed")
    return r["rp2350"]


@pytest.fixture
def mirror(renderer):
    try:
        b = Board(PORT)
    except serial.SerialException as e:
        pytest.skip(f"{PORT} is busy (quit the Touch Deck app): {e}")
    m = Mirror(b, renderer)
    b.send("WATCH 1")
    m.pump(0.4)
    try:
        yield m
    finally:
        b.send("ANIM 0"); b.send("CLIP CLEAR"); b.goto(CLIP); b.send("WATCH 0"); b.pump(0.3)
        b.s.close()


def settle(m, page, secs=3.0):
    m.b.goto(page)
    m.pump(secs)                                   # messages expire after 2.5 s
    assert m.st.screen == page


def board_crc(m):
    return m.fbcrc(0, 0, 240, 240)


def test_clipboard_page_is_pixel_identical(mirror):
    put_clip(mirror.b, "Mirror check\r\nwraps a long line of text across the box, then\n\ttabs")
    settle(mirror, CLIP)
    assert mirror.st.clip_len > 40
    assert board_crc(mirror) == mirror.r.crc(mirror.r.render(mirror.st))


def test_empty_clipboard_is_pixel_identical(mirror):
    mirror.b.send("CLIP CLEAR")
    settle(mirror, CLIP)
    assert board_crc(mirror) == mirror.r.crc(mirror.r.render(mirror.st))


def test_jiggler_page_is_pixel_identical_for_each_scale(mirror):
    settle(mirror, JIG)
    if mirror.st.jig_on:
        pytest.skip("the jiggler is on (its dot and countdown move); not switching it off")
    start = mirror.st.jig_scale
    try:
        for _ in range(3):
            mirror.b.send("BTN")                   # BOOT on the jiggler page: next scale
            mirror.pump(0.8)
            assert board_crc(mirror) == mirror.r.crc(mirror.r.render(mirror.st)), f"scale {mirror.st.jig_scale}"
    finally:
        while mirror.st.jig_scale != start:
            mirror.b.send("BTN"); mirror.pump(0.3)


def test_jiggler_settings_page_is_pixel_identical(mirror):
    """The Jiggler settings page (firmware 1.8.0), with the menu on and off (dimmed row)."""
    start = None
    try:
        settle(mirror, JIG, 1.0)
        mirror.b.send("TAP 52 172"); mirror.pump(0.6)      # open the panel
        assert mirror.st.sub == 2
        start = (mirror.st.jig_menu_on, mirror.st.jig_key, mirror.st.jig_open_s, mirror.st.jig_pause_s)
        for cfg in ("1 0 2 0", "0 1 17 9"):
            mirror.b.send("JIG CFG " + cfg)
            mirror.pump(0.8)
            assert board_crc(mirror) == mirror.r.crc(mirror.r.render(mirror.st)), cfg
    finally:
        if start:
            mirror.b.send("JIG CFG %d %d %d %d" % start); mirror.pump(0.3)
        mirror.b.send("SWIPE R"); mirror.pump(0.3)                      # close it


def test_watch_page_is_pixel_identical(mirror):
    """The round watch (1.8.0): hands and ticks to the pixel. Right after showing a second the board
    draws the next one ahead into its framebuffer, so fb holds either second t or t+1."""
    import time as _t
    settle(mirror, WATCH, 1.5)
    for _ in range(3):
        t0 = mirror.st.time_s
        end = _t.time() + 2
        while mirror.st.time_s == t0 and _t.time() < end:
            mirror.pump(0.02)
        mirror.pump(0.35)
        got, t = board_crc(mirror), mirror.st.time_s
        crcs = set()
        for tt in (t, (t + 1) % 86400):
            mirror.st.time_s = tt
            crcs.add(mirror.r.crc(mirror.r.render(mirror.st)))
        mirror.st.time_s = t
        assert got in crcs, f"t={t}"
