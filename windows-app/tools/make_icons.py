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
INNER = (16, 21, 30, 255)
PC = (242, 163, 58, 255)        # amber accent (PC mode on the device)
OK = (60, 203, 127, 255)
IDLE = (138, 148, 166, 255)
BAD = (229, 72, 77, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw(size, dot):
    """The jiggler page as an icon: rounded-square device body with the amber edge
    ring, an active letter lane "T", and the dot resting in the rounded end of the
    T's top bar (where it would turn back), with equal space around it."""
    s = 8                                        # supersample, then downscale for anti-aliasing
    n = size * s
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 24                           # tray/taskbar sizes: bolder strokes, no fine walls
    r = n * 0.22
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=r, fill=BG)
    ring = n * 0.045 if small else max(n * 0.06, 1.3 * s)
    inset = n * 0.03 if small else n * 0.07
    d.rounded_rectangle([inset, inset, n - 1 - inset, n - 1 - inset], radius=r * 0.8, outline=PC, width=int(ring))

    lane = n * (0.19 if small else 0.17)         # inner width of the lane
    wall = n * (0.07 if small else 0.035)
    top, bottom = n * (0.30 if small else 0.33), n * (0.80 if small else 0.76)
    left, right, mid = n * (0.20 if small else 0.27), n * (0.80 if small else 0.73), n * 0.5
    strokes = [((left, top), (right, top)), ((mid, top), (mid, bottom))]

    def capsule(a, b, width, fill):
        rad = width / 2
        d.line([a, b], fill=fill, width=int(width))
        for x, y in (a, b):
            d.ellipse([x - rad, y - rad, x + rad, y + rad], fill=fill)

    for a, b in strokes:
        capsule(a, b, lane + 2 * wall, PC)       # walls
    for a, b in strokes:
        capsule(a, b, lane, INNER)               # the lane
    dr = lane * (0.42 if small else 0.30)        # dot: centred in the bar's right end cap
    d.ellipse([right - dr, top - dr, right + dr, top + dr], fill=dot)
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
