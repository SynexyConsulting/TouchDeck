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
