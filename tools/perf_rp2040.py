"""Frame-time report for the RP2040 Touch Deck, from the firmware's DBG counters.
    python tools/perf_rp2040.py [--rounds N] [--watch]     (quit the Touch Deck app first)

Two tables:
  full frames  each page is fully redrawn 20 times per round (a TIME command
               redraws the page and changes nothing else); DBG's drawmax after
               each one is that frame's draw time: median, min and worst.
  animation    what the board draws by itself for 5 s on the watch (a partial
               frame per second) and on the jiggler page with ANIM 1 (partial
               frames at 20 fps, no HID): frames per second and draw times.
"""
import argparse
import os
import sys
import time
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "tests"))
from test_board_pc_mode import Board
from touchdeck import PID, VID, find_port

PAGES = [(0, "watch"), (1, "clipboard"), (2, "jiggler")]
REDRAWS = 20


def get(d, k):
    return int(d.split(f" {k}=")[1].split()[0])


def force_full(b):
    b.send(time.strftime("TIME %H:%M:%S"))    # clock_set() -> full redraw, nothing else changes


def full_frames(b, idx):
    """Draw times (ms) of REDRAWS single full redraws: DBG's drawmax covers exactly one."""
    b.goto(idx)
    b.pump(1.2)
    out = []
    for _ in range(REDRAWS):
        b.dbg()                               # resets drawmax
        force_full(b)
        b.pump(0.7)                           # the slowest letters take ~370 ms
        out.append(get(b.dbg(), "drawmax") / 1000)
    return out


def animation(b, idx, cmd, secs=5.0):
    b.goto(idx)
    if cmd:
        b.send(cmd)
    b.pump(1.5)
    d = b.dbg()
    f0, t0 = get(d, "frames"), time.time()
    b.pump(secs)
    d = b.dbg()
    fps = (get(d, "frames") - f0) / (time.time() - t0)
    if cmd:
        b.send(cmd.split()[0] + " 0")
        b.pump(0.3)
    return fps, get(d, "draw") / 1000, get(d, "drawmax") / 1000


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rounds", type=int, default=3)
    ap.add_argument("--watch", action="store_true", help="stream the device mirror (WATCH 1) as the app does")
    args = ap.parse_args()
    b = Board(find_port(VID, PID))
    try:
        if args.watch:
            b.send("WATCH 1")
        full = {}
        anim = {"watch": [], "jig anim": []}
        for _ in range(args.rounds):
            for idx, name in PAGES:
                if idx == 2:                  # the jiggler's cost depends on its letter (ANIM picks new ones)
                    name += " " + b.field("letter")
                full.setdefault(name, []).append(full_frames(b, idx))
            anim["watch"].append(animation(b, 0, None))
            anim["jig anim"].append(animation(b, 2, "ANIM 1"))
        print(f"full frames ({args.rounds} rounds x {REDRAWS} redraws)")
        for name, rows in full.items():
            s = sorted(x for r in rows for x in r)
            print(f"  {name:10s} draw median {s[len(s) // 2]:6.1f} ms  min {s[0]:6.1f} ms  worst {s[-1]:6.1f} ms")
        print("animation")
        for name, rows in anim.items():
            fps = sum(r[0] for r in rows) / len(rows)
            draw = sum(r[1] for r in rows) / len(rows)
            worst = max(r[2] for r in rows)
            print(f"  {name:10s} {fps:5.2f} fps  draw {draw:6.1f} ms  worst draw {worst:6.1f} ms")
        b.goto(0)
    finally:
        b.s.close()


if __name__ == "__main__":
    main()
