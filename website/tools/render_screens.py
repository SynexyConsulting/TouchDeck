"""Renders the Touch Deck screens for the website from the firmware's own page code.

    python website/tools/render_screens.py            # writes website/assets/screens/*.png

It builds the device renderers (hostui/build.bat with MSVC on Windows, hostui/build.sh
elsewhere), the same libraries the Windows app's live mirror uses, fills a ui_state_t
for each page and saves the RGB565 frame as a PNG (and a few animated WebPs).
So the pictures on the site are pixel for pixel what the boards draw.

Needs Pillow (python -m pip install pillow) and a C compiler.
"""
import math
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(ROOT, "website", "assets", "screens")
sys.path.insert(0, os.path.join(ROOT, "tools", "tests"))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import tdui_host                      # noqa: E402  (tools/tests)
import jigpaths                       # noqa: E402  (tools)
from PIL import Image                 # noqa: E402

# Page indices per board (each board's own order).
PAGES = {
    "rp2040": dict(watch=0, clip=1, jig=2),
    "rp2350": dict(watch=0, clip=1, jig=2),
}
WATCH_T = 10 * 3600 + 9 * 60 + 36          # 10:09:36, the watchmaker's pose
CLIP = b"ssh deploy@10.0.4.12 -p 2222\nexport API_TOKEN=tdk_9f2c41e07ab3"


def state(**kw):
    st = tdui_host.UiState()
    st.link_ok = 1
    st.helper = 1
    st.clip_src = b"-"
    st.jig_menu_on, st.jig_key, st.jig_open_s, st.jig_pause_s = 1, 0, 2, 0
    for k, v in kw.items():
        setattr(st, k, v)
    return st


def to_image(r, frame):
    img = Image.new("RGB", (r.w, r.h))
    px = []
    for i in range(0, len(frame), 2):
        p = frame[i] | frame[i + 1] << 8
        px.append(((p >> 11) * 255 // 31, ((p >> 5) & 63) * 255 // 63, (p & 31) * 255 // 31))
    img.putdata(px)
    return img


def letter_point(name, frac):
    """A point frac (0..1) of the way along letter name's centre line, in box units."""
    for n, pts, closed, cum in jigpaths.letters():
        if n != name:
            continue
        d = frac * cum[-1]
        segs = jigpaths.segments(pts, closed)
        for i, (a, b) in enumerate(segs):
            if cum[i + 1] >= d:
                t = (d - cum[i]) / max(1e-6, cum[i + 1] - cum[i])
                return a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t
        return pts[-1]
    raise KeyError(name)


def letter_index(name):
    return [n for n, *_ in jigpaths.LETTERS].index(name)


def bounce(frac):
    """Open letters bounce: 0 -> 1 -> 0."""
    return 1 - abs(1 - 2 * frac)


def main():
    tmp = tempfile.mkdtemp(prefix="tdui_")
    renderers = tdui_host.build(tmp)
    if renderers is None:
        sys.exit("render_screens: no C compiler found (MSVC on Windows, cc elsewhere)")
    os.makedirs(OUT, exist_ok=True)
    shapes = {"rect": "rp2040", "round": "rp2350"}
    for shape, board in shapes.items():
        r = renderers[board]
        pg = PAGES[board]

        def save(name, **kw):
            img = to_image(r, r.render(state(**kw)))
            img.save(os.path.join(OUT, f"{shape}-{name}.png"), optimize=True)
            return img

        save("watch", screen=pg["watch"], time_s=WATCH_T)
        save("watch-stopwatch", screen=pg["watch"], time_s=WATCH_T, timer_s=83)
        save("clipboard", screen=pg["clip"], clip=CLIP, clip_len=len(CLIP), clip_src=b"PC")
        save("clipboard-paste", screen=pg["clip"], clip=CLIP, clip_len=len(CLIP), clip_src=b"PC",
             clip_state=2, paste_pos=31)
        x, y = letter_point("M", 0.3)
        save("jiggler", screen=pg["jig"], jig_on=1, jig_letter=letter_index("M"), jig_x=x, jig_y=y,
             jig_next_s=74, jig_up_s=3 * 3600 + 25 * 60, jig_menus=41)
        save("jiggler-menu", screen=pg["jig"], sub=2, jig_on=1)

        # Animated jiggler: the dot walks letter O, then W (40 frames each, 20 fps).
        frames = []
        for name, closed in (("O", True), ("W", False)):
            for i in range(40):
                f = i / 40
                x, y = letter_point(name, f if closed else bounce(f))
                frames.append(to_image(r, r.render(state(
                    screen=pg["jig"], jig_on=1, jig_letter=letter_index(name), jig_x=x, jig_y=y,
                    jig_next_s=74 - i // 20, jig_up_s=3 * 3600 + 25 * 60 + i // 20, jig_menus=41))))
        frames[0].save(os.path.join(OUT, f"{shape}-jiggler-anim.webp"), save_all=True,
                       append_images=frames[1:], duration=50, loop=0, lossless=True)

        # Animated watch: one minute of ticks, 1 frame per second.
        frames = [to_image(r, r.render(state(screen=pg["watch"], time_s=WATCH_T + s, timer_s=83 + s)))
                  for s in range(60)]
        frames[0].save(os.path.join(OUT, f"{shape}-watch-anim.webp"), save_all=True,
                       append_images=frames[1:], duration=1000, loop=0, lossless=True)
    # ESP32-C3 (round, Bluetooth): Clipboard in Bluetooth mode (blue accent) and Settings.
    r = renderers["esp32c3"]
    for name, kw in (("clipboard-bt", dict(screen=1, clip=CLIP, clip_len=len(CLIP), clip_src=b"PC")),
                     ("settings-bt", dict(screen=3))):
        img = to_image(r, r.render(state(bt_mode=1, bt_avail=1, bt_state=3, bt_ready=1, bt_host=b"DESK-PC", **kw)))
        img.save(os.path.join(OUT, f"esp32-{name}.png"), optimize=True)
    print("render_screens: wrote", OUT)


if __name__ == "__main__":
    main()
