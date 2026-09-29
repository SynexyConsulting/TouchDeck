"""Frame-time report for the RP2040 Touch Deck: per page, average draw and
push time (ms) and the worst draw, from the firmware's DBG counters.
    py tools/perf_rp2040.py        (quit the Touch Deck app first)"""
import os, sys, time
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "tests"))
from test_board_pc_mode import Board
from touchdeck import PID, VID, find_port

PAGES = [(0, "watch", None), (1, "clipboard", None), (2, "jiggler", None), (2, "jig anim", "ANIM 1")]

def main():
    b = Board(find_port(VID, PID))
    try:
        for idx, name, cmd in PAGES:
            b.goto(idx)
            if cmd:
                b.send(cmd)                     # e.g. spin the jiggler dot without sending HID
            b.pump(2.5)                         # let the averages settle on this page
            d = b.dbg()
            get = lambda k: int(d.split(f" {k}=")[1].split()[0])
            f0, t0 = get("frames"), time.time()
            b.pump(3.0)
            d = b.dbg()
            fps = (get("frames") - f0) / (time.time() - t0)
            print(f"{name:10s} draw {get('draw') / 1000:6.1f} ms  push {get('push') / 1000:5.1f} ms  "
                  f"worst draw {get('drawmax') / 1000:6.1f} ms  {fps:4.1f} fps")
            if cmd:
                b.send(cmd.split()[0] + " 0")
        b.goto(0)
    finally:
        b.s.close()

if __name__ == "__main__":
    main()
