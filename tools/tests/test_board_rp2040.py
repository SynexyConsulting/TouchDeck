"""Drives the RP2040 1.69" Touch Deck (USB CAFE:4011) over its serial port.
Only uses the watch and page navigation, so nothing is typed or moved on the PC."""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
from touchdeck import PID, VID, find_port
from test_board_pc_mode import Board

RP_PORT = find_port(VID, PID)
pytestmark = pytest.mark.skipif(RP_PORT is None, reason="RP2040 Touch Deck not connected")

@pytest.fixture
def board():
    b = Board(RP_PORT)
    try:
        yield b
    finally:
        b.s.close()

def test_boot_button_cycles_jiggler_scale(board):
    board.goto(2)                                        # watch, clipboard, JIGGLER
    assert board.field("screen") == "2"
    seen = [board.field("jscale")]
    for _ in range(3):
        board.send("BTN"); board.pump(0.2)
        seen.append(board.field("jscale"))
    order = ["1.0", "1.5", "2.0"]
    i = order.index(seen[0])
    assert seen == [order[(i + k) % 3] for k in range(4)]   # cycles and wraps back

def test_boot_button_runs_watch_stopwatch(board):
    board.goto(0)
    board.send("BTN LONG"); board.pump(0.2)               # long press resets
    assert board.field("timer") == "0" and board.field("trun") == "0"
    board.send("BTN"); board.pump(2.3)                    # start
    assert board.field("trun") == "1"
    assert int(board.field("timer")) >= 2
    board.send("BTN"); board.pump(0.2)                    # pause
    t = board.field("timer")
    board.pump(1.5)
    assert board.field("trun") == "0" and board.field("timer") == t
    board.send("BTN LONG"); board.pump(0.2)
    assert board.field("timer") == "0"

def test_button_is_ignored_on_clipboard_page(board):
    board.goto(1)
    before = (board.field("jscale"), board.field("trun"))
    board.send("BTN"); board.pump(0.2)
    assert (board.field("jscale"), board.field("trun")) == before

def test_watch_second_is_pushed_on_the_timer_edge(board):
    """The tick plays from a hardware-timer alarm on the second; the frame for
    that second is drawn ahead and pushed right away, so screen and sound agree."""
    board.goto(0)
    board.pump(4.5)                                       # a few seconds on the watch page
    lag_us = int(board.field("lag"))                      # edge -> drawn-ahead frame on screen (avg)
    hits, misses = int(board.field("hits")), int(board.field("miss"))
    assert hits >= 3, (hits, misses)
    assert hits > misses
    assert lag_us < 20000

def test_ver_reports_board_and_firmware_version(board):
    board.take(); board.send("VER"); board.pump(0.4)
    replies = [l for l in board.take() if l.startswith("VERSION ")]
    assert replies, "no VERSION reply"
    _, name, semver, *build = replies[-1].split()
    assert name == "rp2040-169"
    assert len(semver.split(".")) == 3 and all(p.isdigit() for p in semver.split("."))
    assert build                                          # compile date

def test_dbg_reports_a_jiggler_letter(board):
    assert board.field("letter") in "OWMNZXCVHJLBGD"

def put_clip(board, text):
    board.send(f"CLIP {len(text)} test"); board.s.write(text.encode()); board.pump(0.3)

def test_trash_tap_clears_the_clip(board):
    board.goto(1)                                         # clipboard
    put_clip(board, "hello")
    assert board.field("clip") == "5"
    board.send("TAP 120 120"); board.pump(0.2)           # the text box: nothing happens
    assert board.field("clip") == "5"
    board.send("TAP 196 43"); board.pump(0.3)            # the trash can
    assert board.field("clip") == "0"

def test_scale_pill_tap_cycles_like_the_button(board):
    board.goto(2)
    before = board.field("jscale")
    board.send("TAP 39 42"); board.pump(0.3)             # scale pill, top-left
    after = board.field("jscale")
    order = ["1.0", "1.5", "2.0"]
    assert after == order[(order.index(before) + 1) % 3]

def test_letter_zone_tap_toggles_the_jiggler(board):
    """Moves the real mouse for ~0.3 s (the RP2040 is a USB mouse)."""
    board.goto(2)
    was = board.field("jig")
    board.send("TAP 120 140"); board.pump(0.3)
    assert board.field("jig") != was
    board.send("TAP 120 140"); board.pump(0.3)
    assert board.field("jig") == was

def state_lines(board, secs):
    board.take(); board.pump(secs)
    return [l for l in board.take() if l.startswith("STATE ")]

def fields(line):
    return dict(kv.split("=", 1) for kv in line.split()[1:])

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
    board.send("WATCH 1")
    start = int(fields(state_lines(board, 0.4)[-1])["scale"])
    try:
        for want in (2, 0, 1):
            board.send(f"JIG SCALE {want}"); board.pump(0.2)
            assert board.field("jscale") == ["1.0", "1.5", "2.0"][want]
    finally:
        board.send(f"JIG SCALE {start}"); board.send("WATCH 0"); board.pump(0.2)

def test_jig_on_off_command(board):
    """Moves the real mouse a few px for ~0.3 s (the RP2040 is a USB mouse)."""
    was = board.field("jig")
    try:
        board.send("JIG ON"); board.pump(0.3)
        assert board.field("jig") == "1"
        board.send("JIG OFF"); board.pump(0.2)
        assert board.field("jig") == "0"
    finally:
        board.send("JIG ON" if was == "1" else "JIG OFF"); board.pump(0.2)

def latest_state(board):
    """Forces a fresh report (WATCH 1 re-sends once) and returns its fields."""
    board.send("WATCH 1")
    return fields(state_lines(board, 0.3)[-1])

def test_clip_clear_ignored_while_empty(board):
    put_clip(board, "hello")
    assert latest_state(board)["clip"] == "5"
    board.send("CLIP CLEAR"); board.pump(0.3)
    assert latest_state(board)["clip"] == "0"
    board.send("CLIP CLEAR"); board.pump(0.3)          # empty: a no-op, no error
    assert latest_state(board)["clip"] == "0"
    board.send("WATCH 0"); board.pump(0.2)
