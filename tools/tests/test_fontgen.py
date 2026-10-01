import os, random, shutil, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import fontgen

FONTS = {s.name: fontgen.render_font(s) for s in fontgen.FONTS}

def test_advance_matches_pillow():
    spec = next(s for s in fontgen.FONTS if s.name == "font_title")
    pil = fontgen.load_pil(spec)
    assert FONTS["font_title"].glyphs["W"].adv == round(pil.getlength("W"))

def test_pack_roundtrip():
    rnd = random.Random(1)
    w, h = 5, 3
    alpha = [rnd.randrange(16) for _ in range(w * h)]
    assert fontgen.unpack4(fontgen.pack4(alpha, w, h), w, h) == alpha

def test_glyphs_are_antialiased():
    o = FONTS["font_title"].glyphs["O"].alpha
    assert any(0 < a < 15 for a in o) and max(o) == 15

def test_timer_font_has_seven_segment_digits():
    g = FONTS["font_timer"].glyphs
    assert all(g[c].w and g[c].h for c in "0123456789:")
    assert FONTS["font_timer"].spec.size == 2 * next(s.size for s in fontgen.FONTS if s.name == "font_caps")

def test_mono_font_has_fixed_advance():
    g = FONTS["font_mono"].glyphs
    assert len({g[c].adv for c in "iW.m0"}) == 1

@pytest.mark.skipif(shutil.which("arm-none-eabi-gcc") is None and
                    not os.path.exists(os.path.expanduser(r"~\.pico-sdk\toolchain\14_2_Rel1\bin\arm-none-eabi-gcc.exe")),
                    reason="no ARM compiler")
def test_generated_c_compiles(tmp_path):
    h, c = fontgen.emit_c(list(FONTS.values()))
    (tmp_path / "aa_fonts.h").write_text(h)
    (tmp_path / "aa_fonts.c").write_text(c)
    gcc = shutil.which("arm-none-eabi-gcc") or os.path.expanduser(r"~\.pico-sdk\toolchain\14_2_Rel1\bin\arm-none-eabi-gcc.exe")
    r = subprocess.run([gcc, "-std=c11", "-Wall", "-Werror", "-fsyntax-only", str(tmp_path / "aa_fonts.c")],
                       capture_output=True, text=True)
    assert r.returncode == 0, r.stderr

# Every label drawn by the UIs must fit the box (or circle chord) it sits in.
# Pill buttons: icon slot 10 px + 4 px gap, 6 px side padding (segments: 4 px).
# (font, text, available width px, letter spacing)
ICON = 10 + 4
LABELS = [
    ("font_button", "Copy", 79 - ICON - 12, 0),
    ("font_button", "Paste", 79 - ICON - 12, 0),
    ("font_button", "Stop", 79 - 12, 0),
    ("font_button", "Disconnect", 77 - 12, 0),
    ("font_button", "Connect", 77 - 12, 0),
    ("font_button", "Forget", 77 - 12, 0),
    ("font_button", "Bluetooth", 78 - ICON - 8, 0),   # settings toggle segment
    ("font_button", "Cancel", 108 - 12, 0),
    ("font_caps", "BLUETOOTH", 64, 1),                # top chip label
    ("font_title", "DESKTOP-WXYZ-LONGER", 190, 0),   # host name (truncated to 18 chars on device)
    ("font_label", "Waiting for Bluetooth", 190, 0),  # jiggler status at y 184
    ("font_body", "Pair Bluetooth to switch", 230, 0),
    ("font_body", "Menu every 45-150s", 160, 0),     # jiggler stats at y 200
    ("font_big", "OFF", 80, 0),                       # inside the orbit's inner disc
    ("font_body", "PC: Settings > Bluetooth >", 210, 0),       # bluetooth page, y 152
    ("font_body", 'Pick "Touch Deck" in Add device', 225, 0),  # pairing page, y 130
    ("font_body", "Enter this PIN on your PC", 190, 0),        # pairing page, y 72
    ("font_caps", "DISCONNECTED", 120, 1),
    ("font_timer", "88:88:88", 104, 0),                        # stopwatch box on the watch face
    ("font_caps", "2.0X", 36 - 8, 1),                          # jiggler scale pill
    ("font_caps", "OFF", 44 - 12, 1),                          # jiggler ON/OFF pill (round is narrowest)
    ("font_caps", "2.0X", 44 - 10, 1),                         # scale pill beside/above the letter
    ("font_body", "Cleared", 150, 0),                          # clipboard status line
    # Jiggler menu panel (round is tightest: rows from x 30, controls from x 126)
    ("font_title", "Jiggler menu", 112, 0),                    # title between the X (x <= 59) and x 176
    ("font_label", "Context menu", 124, 0),                    # label x 30 .. toggle x 160
    ("font_label", "Menu open", 92, 0),                        # label x 30 .. stepper x 126
    ("font_label", "Pause", 92, 0),
    ("font_caps", "ESC", 40 - 8, 1),                           # key segments, 40 wide
    ("font_caps", "F15", 40 - 8, 1),
    ("font_caps", "OFF", 48 - 12, 1),                          # context menu toggle, 48 wide
    ("font_label", "60 s", 30, 0),                             # stepper value between - and +
    ("font_label", "-", 26 - 8, 0),                            # stepper buttons, 26 wide
    ("font_label", "+", 26 - 8, 0),
    ("font_body", "Right-click, wait, Esc", 170, 0),           # round hint, y 202
    ("font_body", "Right-click, wait, F15", 170, 0),
    ("font_body", "Right-click, wait, Esc, pause", 196, 0),    # rect hint, y 240
    ("font_caps", "TIME FROM PC", 150, 1),                     # round watch before the app sends TIME, y 82
]

@pytest.mark.parametrize("font,text,avail,spacing", LABELS)
def test_labels_fit(font, text, avail, spacing):
    t = text[:18] if font == "font_title" else text
    assert fontgen.text_width(FONTS[font], t, spacing) <= avail
