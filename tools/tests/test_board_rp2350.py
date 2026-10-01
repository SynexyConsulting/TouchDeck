"""Drives the round RP2350 1.28" Touch Deck (USB CAFE:4011, VER rp2350-128) over
its serial port. Pages: 0 Clipboard, 1 Jiggler (no watch, no buzzer). Tap
coordinates come from src/round/ui.h (the ESP32-C3's round layout)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import serial
from touchdeck import find_board
from test_board_pc_mode import Board

PORT = find_board("rp2350-128")
pytestmark = pytest.mark.skipif(PORT is None, reason="RP2350 round Touch Deck not connected")

WATCH, CLIP, JIG = 0, 1, 2
TRASH = (178, 42)          # TRASH_CX, TRASH_CY
SCALE_PILL = (32, 120)     # SCALE_PILL_X 10 + 44/2, PILL_Y 111 + 18/2
LETTER = (120, 115)        # JIG_BOX_X 82 + 76/2, JIG_BOX_Y 77 + 76/2


@pytest.fixture
def board():
    try:
        b = Board(PORT)
    except serial.SerialException as e:
        pytest.skip(f"{PORT} is busy (quit the Touch Deck app): {e}")
    try:
        yield b
    finally:
        b.s.close()


def put_clip(board, text):
    board.send(f"CLIP {len(text)} test"); board.s.write(text.encode()); board.pump(0.3)


def state_lines(board, secs):
    board.take(); board.pump(secs)
    return [l for l in board.take() if l.startswith("STATE ")]


def fields(line):
    return dict(kv.split("=", 1) for kv in line.split()[1:])


def test_ver_reports_the_round_board(board):
    board.take(); board.send("VER"); board.pump(0.4)
    replies = [l for l in board.take() if l.startswith("VERSION ")]
    assert replies and replies[-1].split()[1] == "rp2350-128"


def test_three_pages_and_swipes_stop_at_the_ends(board):
    board.goto(WATCH)
    assert board.field("screen") == "0"
    board.send("SWIPE R"); board.pump(0.2)
    assert board.field("screen") == "0"
    for page in ("1", "2", "2"):                      # Clipboard, Jiggler, and no fourth page
        board.send("SWIPE L"); board.pump(0.2)
        assert board.field("screen") == page


def test_boot_button_runs_the_watch_stopwatch(board):
    """As on the 1.69: on the watch, BOOT starts/pauses the stopwatch, a long press resets it."""
    board.goto(WATCH)
    board.send("BTN LONG"); board.pump(0.2)
    assert board.field("timer") == "0" and board.field("trun") == "0"
    board.send("BTN"); board.pump(2.3)
    assert board.field("trun") == "1" and int(board.field("timer")) >= 2
    board.send("BTN"); board.pump(0.2)
    board.send("BTN LONG"); board.pump(0.2)
    assert board.field("timer") == "0" and board.field("trun") == "0"


def test_touch_chip_answers(board):
    assert board.field("fails") == "0"
    assert int(board.field("chip")) in (0xB4, 0xB5, 0xB6)   # CST816S / T / D


def test_trash_tap_clears_the_clip(board):
    board.goto(CLIP)
    put_clip(board, "hello")
    assert board.field("clip") == "5"
    board.send("TAP 120 100"); board.pump(0.2)         # the text box: nothing happens
    assert board.field("clip") == "5"
    board.send("TAP %d %d" % TRASH); board.pump(0.3)
    assert board.field("clip") == "0"


def test_scale_pill_and_boot_button_cycle_the_scale(board):
    board.goto(JIG)
    order = ["1.0", "1.5", "2.0"]
    before = board.field("jscale")
    board.send("TAP %d %d" % SCALE_PILL); board.pump(0.3)
    after = board.field("jscale")
    assert after == order[(order.index(before) + 1) % 3]
    board.send("BTN"); board.pump(0.2)
    assert board.field("jscale") == order[(order.index(after) + 1) % 3]
    board.send("BTN"); board.pump(0.2)                  # back where it started
    assert board.field("jscale") == before


def test_button_is_ignored_on_clipboard_page(board):
    board.goto(CLIP)
    before = board.field("jscale")
    board.send("BTN"); board.pump(0.2)
    assert board.field("jscale") == before


def test_letter_tap_toggles_the_jiggler(board):
    """Moves the real mouse for ~0.3 s (the RP2350 is a USB mouse)."""
    board.goto(JIG)
    was = board.field("jig")
    board.send("TAP %d %d" % LETTER); board.pump(0.3)
    assert board.field("jig") != was
    board.send("TAP %d %d" % LETTER); board.pump(0.3)
    assert board.field("jig") == was


def test_watch_streams_state_with_the_page(board):
    board.goto(JIG)
    board.send("WATCH 1")
    lines = state_lines(board, 0.5)
    assert lines, "no STATE after WATCH 1"
    f = fields(lines[-1])
    assert set(f) >= {"jig", "letter", "scale", "phase", "x", "y", "clip", "paste", "page"}
    assert f["page"] == str(JIG)
    board.send("WATCH 0"); board.pump(0.2)
    assert not state_lines(board, 0.6), "STATE kept coming after WATCH 0"


def test_anim_demo_moves_the_dot(board):
    board.send("WATCH 1"); board.send("ANIM 1")
    try:
        pts = {(fields(l)["x"], fields(l)["y"]) for l in state_lines(board, 1.2)}
        assert len(pts) >= 5, "dot position did not stream"
    finally:
        board.send("ANIM 0"); board.send("WATCH 0"); board.pump(0.2)


def test_clip_clear_command(board):
    put_clip(board, "hello")
    assert board.field("clip") == "5"
    board.send("CLIP CLEAR"); board.pump(0.3)
    assert board.field("clip") == "0"
    board.send("CLIP CLEAR"); board.pump(0.3)          # empty: a no-op
    assert board.field("clip") == "0"


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
        board.goto(JIG)
        assert board.field("screen") == "2" and board.field("jset") == "0"
        board.send("TAP 52 172"); board.pump(0.25)            # the cog opens the panel
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
        assert board.field("jset") == "0" and board.field("screen") == "2"
        board.send("TAP 52 172"); board.pump(0.25)
        board.send("SWIPE R"); board.pump(0.25)                        # so does a right swipe
        assert board.field("jset") == "0" and board.field("screen") == "2"
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


def test_an_idle_board_is_quiet_while_watched(board):
    """With the app watching (WATCH 1), a board that isn't changing sends STATE once, not on every poll."""
    board.send("JIG OFF"); board.goto(CLIP); board.pump(0.5)
    board.send("WATCH 1"); board.pump(0.6); board.take()
    board.pump(1.0)
    n = len([l for l in board.take() if l.startswith("STATE ")])
    board.send("WATCH 0"); board.pump(0.2)
    assert n <= 2, f"{n} STATE lines in 1 s from an idle board"
