import os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import clip_helper
from inject import Injector

class FakeSerial:
    def __init__(self): self.out = b""
    def write(self, b): self.out += b

def setup():
    return FakeSerial(), Injector(dry_run=True, echo=False)

def test_key_line_becomes_key_report():
    ser, inj = setup()
    clip_helper.handle_line("K 02 04", ser, inj)
    assert inj.held_key == 0x04 and inj.held_mods == 0x02

def test_mouse_line_parses_signed_decimal():
    ser, inj = setup()
    clip_helper.handle_line("M 00 -7 12", ser, inj)
    assert inj.log == [("move", -7, 12)]

def test_malformed_lines_are_ignored():
    ser, inj = setup()
    for bad in ["K zz 04", "K 02", "M 00 x 1", "M", "K 00 99"]:
        clip_helper.handle_line(bad, ser, inj)   # must not raise
    assert inj.log == []

def test_out_of_range_values_are_ignored():
    ser, inj = setup()
    for bad in ["K -1 04", "K 100 04", "K 00 -4", "M -1 0 0", "M 1FF 0 0"]:
        clip_helper.handle_line(bad, ser, inj)   # must not press modifiers/buttons
    assert inj.log == []

def test_copy_request_sends_clip(monkeypatch):
    ser, inj = setup()
    monkeypatch.setattr(clip_helper, "grab_text", lambda: ("hi", "select"))
    clip_helper.handle_line("COPY", ser, inj)
    assert ser.out == b"CLIP 2 select\nhi"
