"""Checks the Windows app's device mirror against the real board.

    TouchDeck.exe --smoke DIR --smoke-steps      (drives the board, saves DIR/mirror-*.png, quits)
    python tools/mirror_check.py DIR

The app's last step leaves the board on the clipboard page with a clip it sent. This compares
the app's picture of that page (mirror-clip.png) with the board's own framebuffer (FBCRC),
checks that ANIM moved the dot between mirror-anim1.png and mirror-anim2.png, then clears the
clip and returns the board to its first page. Needs Pillow (tools/requirements.txt).
"""
import os
import sys
import time
import zlib

from PIL import Image

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "tests"))
from test_board_pc_mode import Board
from touchdeck import find_port


def rgb565(path):
    """PNG -> the RGB565 bytes the board has (the app saved it from an RGB565 frame)."""
    im = Image.open(path).convert("RGB")
    out = bytearray()
    for r, g, b in im.getdata():
        v = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)
        out += bytes((v & 0xFF, v >> 8))
    return im.size, bytes(out)


def main():
    d = sys.argv[1]
    (w, h), clip = rgb565(os.path.join(d, "mirror-clip.png"))
    a1, a2 = (rgb565(os.path.join(d, f"mirror-anim{i}.png"))[1] for i in (1, 2))
    ok = True
    if a1 == a2:
        print("FAIL: the jiggler dot did not move between mirror-anim1.png and mirror-anim2.png")
        ok = False
    else:
        print("ok: the mirror animated the jiggler dot")
    b = Board(find_port())
    try:
        b.take()
        b.send(f"FBCRC 0 0 {w} {h}")
        b.pump(0.5)
        reply = [l for l in b.take() if l.startswith("LOG fbcrc ")]
        board = int(reply[-1].split()[2], 16) if reply else None
        app = zlib.crc32(clip)
        if board == app:
            print(f"ok: the app's clipboard page == the board's framebuffer (crc {app:08x}, {w}x{h})")
        else:
            print(f"FAIL: app {app:08x} != board {board}")
            ok = False
    finally:
        b.send("CLIP CLEAR")
        b.goto(0)
        b.s.close()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
