"""Draw the app and tray icons in the Touch Deck device palette.

Run with the Python that has Pillow:  python tools/make_icons.py
Writes src/TouchDeck.App/Assets/{app,tray-ok,tray-idle,tray-bad}.ico
"""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "TouchDeck.App", "Assets")

BG = (7, 9, 13, 255)
SURF = (20, 26, 36, 255)
PC = (242, 163, 58, 255)        # amber accent (PC mode on the device)
OK = (60, 203, 127, 255)
IDLE = (138, 148, 166, 255)
BAD = (229, 72, 77, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw(size, dot):
    """Rounded device body, amber edge ring, and a status dot (the board's USB chip dot)."""
    s = 8                                        # supersample, then downscale for anti-aliasing
    n = size * s
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    r = n * 0.22
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=r, fill=BG)
    ring = max(n * 0.075, 1.6 * s)               # stays at least ~1.6 px wide at 16 px
    inset = n * 0.09
    d.rounded_rectangle([inset, inset, n - 1 - inset, n - 1 - inset], radius=r * 0.8,
                        outline=PC, width=int(ring))
    # Centre: a filled pill button, as on the device's Clipboard page.
    pw, ph = n * 0.5, n * 0.2
    px, py = (n - pw) / 2, (n - ph) / 2
    d.rounded_rectangle([px, py, px + pw, py + ph], radius=ph / 2, fill=PC)
    # Top right: the status dot, like the USB chip's link dot.
    dr = n * 0.075
    cx, cy = n * 0.68, n * 0.32
    d.ellipse([cx - dr, cy - dr, cx + dr, cy + dr], fill=dot)
    return im.resize((size, size), Image.LANCZOS)


def save(name, dot):
    images = [draw(sz, dot) for sz in SIZES]
    path = os.path.join(OUT, name)
    images[-1].save(path, format="ICO", sizes=[(sz, sz) for sz in SIZES], append_images=images[:-1])
    print("wrote", os.path.relpath(path))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    save("app.ico", PC)
    save("tray-ok.ico", OK)
    save("tray-idle.ico", IDLE)
    save("tray-bad.ico", BAD)
    draw(256, PC).save(os.path.join(OUT, "app-256.png"))
