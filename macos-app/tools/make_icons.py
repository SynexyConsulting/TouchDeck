"""Draw the Mac app icon and the monochrome menu bar icons.

Run with the Python that has Pillow:  python macos-app/tools/make_icons.py
- AppIcon: the Windows app icon (windows-app/tools/make_icons.py draw()) at the Mac sizes.
- MenuBarIcon / MenuBarIconIdle / MenuBarIconBad: template images (black + alpha only; macOS
  tints them for the light or dark menu bar). The same "T" lane with its dot, drawn as a solid
  shape; the three states of the Windows tray icon (connected, looking, port busy / no answer).
"""
import json
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "TouchDeck", "Assets.xcassets")
sys.path.insert(0, os.path.join(HERE, "..", "..", "windows-app", "tools"))
import make_icons as win  # noqa: E402

INK = (0, 0, 0, 255)
CLEAR = (0, 0, 0, 0)


def template(px, bad=False, idle=False):
    """The menu bar glyph at px x px (18 pt at 1x and 2x): rounded-square outline, solid T,
    the jiggler dot punched out of the bar's right end. Bad: no dot, and a strike line.
    Idle (no board yet): no dot, at half strength (template alpha draws it dimmer)."""
    s = 8
    n = px * s
    im = Image.new("RGBA", (n, n), CLEAR)
    d = ImageDraw.Draw(im)
    stroke = n * 0.09
    d.rounded_rectangle([stroke / 2, stroke / 2, n - stroke / 2, n - stroke / 2], radius=n * 0.24, outline=INK, width=int(stroke))
    bar = n * 0.2
    top, bottom, left, right, mid = n * 0.34, n * 0.76, n * 0.27, n * 0.73, n * 0.5

    def capsule(a, b, width, fill):
        r = width / 2
        d.line([a, b], fill=fill, width=int(width))
        for x, y in (a, b):
            d.ellipse([x - r, y - r, x + r, y + r], fill=fill)

    capsule((left, top), (right, top), bar, INK)
    capsule((mid, top), (mid, bottom), bar, INK)
    if bad:
        d.line([(n * 0.12, n * 0.12), (n * 0.88, n * 0.88)], fill=CLEAR, width=int(stroke * 2.2))
        d.line([(n * 0.12, n * 0.12), (n * 0.88, n * 0.88)], fill=INK, width=int(stroke))
    elif not idle:
        dr = bar * 0.28
        d.ellipse([right - dr, top - dr, right + dr, top + dr], fill=CLEAR)
    if idle:
        im.putalpha(im.getchannel("A").point(lambda a: a * 55 // 100))
    return im.resize((px, px), Image.LANCZOS)


def imageset(name, images):
    folder = os.path.join(ASSETS, name + ".imageset")
    os.makedirs(folder, exist_ok=True)
    entries = []
    for scale, im in images:
        fn = f"{name}@{scale}x.png"
        im.save(os.path.join(folder, fn))
        entries.append({"filename": fn, "idiom": "universal", "scale": f"{scale}x"})
    contents = {"images": entries, "info": {"author": "xcode", "version": 1},
                "properties": {"template-rendering-intent": "template"}}
    with open(os.path.join(folder, "Contents.json"), "w", newline="\n") as f:
        json.dump(contents, f, indent=2)
    print("wrote", name)


def app_icon():
    folder = os.path.join(ASSETS, "AppIcon.appiconset")
    os.makedirs(folder, exist_ok=True)
    entries = []
    for pt in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            px = pt * scale
            fn = f"icon_{pt}x{pt}@{scale}x.png"
            # Mac icons sit inside a margin of the canvas (Apple's grid: about 80% body).
            body = win.draw(round(px * 0.8), win.PC)
            canvas = Image.new("RGBA", (px, px), CLEAR)
            off = (px - body.width) // 2
            canvas.paste(body, (off, off), body)
            canvas.save(os.path.join(folder, fn))
            entries.append({"filename": fn, "idiom": "mac", "scale": f"{scale}x", "size": f"{pt}x{pt}"})
    with open(os.path.join(folder, "Contents.json"), "w", newline="\n") as f:
        json.dump({"images": entries, "info": {"author": "xcode", "version": 1}}, f, indent=2)
    print("wrote AppIcon")


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    with open(os.path.join(ASSETS, "Contents.json"), "w", newline="\n") as f:
        json.dump({"info": {"author": "xcode", "version": 1}}, f, indent=2)
    imageset("MenuBarIcon", [(1, template(18)), (2, template(36))])
    imageset("MenuBarIconIdle", [(1, template(18, idle=True)), (2, template(36, idle=True))])
    imageset("MenuBarIconBad", [(1, template(18, bad=True)), (2, template(36, bad=True))])
    app_icon()
    colors = os.path.join(ASSETS, "AccentColor.colorset")
    os.makedirs(colors, exist_ok=True)
    with open(os.path.join(colors, "Contents.json"), "w", newline="\n") as f:
        json.dump({"colors": [{"color": {"color-space": "srgb", "components": {
            "red": "0.949", "green": "0.639", "blue": "0.227", "alpha": "1.000"}}, "idiom": "universal"}],
            "info": {"author": "xcode", "version": 1}}, f, indent=2)
    print("wrote AccentColor")
