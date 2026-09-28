"""The firmware's gfx_text_aa_width() must agree with fontgen's text_width(),
since layout code centres text using it."""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from test_board_pc_mode import Board, PORT, pytestmark  # noqa: F401  (same skip rule)
import pytest
import fontgen

FONTS = {s.name: fontgen.render_font(s) for s in fontgen.FONTS}
CASES = [("font_title", "Clipboard", 0), ("font_button", "Disconnect", 0),
         ("font_caps", "BLUETOOTH", 1), ("font_mono", "ssh deploy@10.0.4.21", 0),
         ("font_big", "OFF", 0), ("font_body", "Pair Bluetooth to switch", 0)]

@pytest.fixture
def board():
    b = Board()
    try:
        yield b
    finally:
        b.s.close()

@pytest.mark.parametrize("font,text,spacing", CASES)
def test_device_text_width_matches_generator(board, font, text, spacing):
    board.take()
    board.send(f"TEXTW {font} {spacing} {text}")
    board.pump(0.3)
    replies = [l for l in board.take() if l.startswith("LOG textw ")]
    assert replies, "no TEXTW reply"
    assert int(replies[-1].split()[-1]) == fontgen.text_width(FONTS[font], text, spacing)
