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
