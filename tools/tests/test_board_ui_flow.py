import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from test_board_pc_mode import Board, PORT, pytestmark  # noqa: F401  (same skip rule)
import pytest

@pytest.fixture
def board():
    b = Board()
    try:
        yield b
    finally:
        b.s.close()

def test_settings_toggle_and_bluetooth_drilldown(board):
    board.goto(2)
    assert board.field("screen") == "2"
    board.send("TAP 160 94"); board.pump(0.2)          # PC segment
    assert board.field("mode") == "0"
    assert board.field("bt_page") == "0"               # the toggle is not the drill-down
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
