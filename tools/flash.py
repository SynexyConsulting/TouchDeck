"""Flash a Touch Deck RP board without touching the BOOT button.
    python tools/flash.py [--board rp2040-169|rp2350-128] [file.uf2]

The board (default rp2040-169) picks the UF2 (build/watch.uf2 or
build-rp2350/deck128.uf2), the port (the one whose VER reports that board)
and the bootloader drive (RPI-RP2 or RP2350).

Gets the board into its UF2 bootloader by whichever route is available:
  1. already in bootloader  -> just copy
  2. Touch Deck firmware    -> send BOOT over its serial port
  3. stock SDK firmware     -> picotool reboot -u
Quit the Touch Deck app first; it holds the serial port.
"""
import os
import shutil
import subprocess
import sys

import argparse

import serial

from touchdeck import SDK_PID, SDK_PID_RP2350, SDK_VID, find_board, find_port, find_uf2_drive, wait_for

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# board -> (default UF2, chip)
BOARDS = {"rp2040-169": (os.path.join(ROOT, "build", "watch.uf2"), "rp2040"),
          "rp2350-128": (os.path.join(ROOT, "build-rp2350", "deck128.uf2"), "rp2350")}
PICOTOOL = os.path.expanduser(r"~\.pico-sdk\picotool\2.1.1\picotool\picotool.exe")


def enter_bootloader(board):
    port = find_board(board)
    if port:
        print(f"Touch Deck on {port}: sending BOOT")
        try:
            with serial.Serial(port, 115200, timeout=1) as s:
                s.write(b"\nBOOT\n")
        except serial.SerialException as e:
            sys.exit(f"Can't open {port} ({e}). Is the Touch Deck app running? Quit it (tray > Quit) and retry.")
        return
    if find_port(SDK_VID, SDK_PID) or find_port(SDK_VID, SDK_PID_RP2350):
        print("Stock SDK firmware: picotool reboot -u")
        subprocess.run([PICOTOOL, "reboot", "-f", "-u"], check=False)
        return
    sys.exit("No board found. Hold BOOT while plugging in, then rerun.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--board", default="rp2040-169", choices=sorted(BOARDS))
    ap.add_argument("uf2", nargs="?")
    args = ap.parse_args()
    default_uf2, chip = BOARDS[args.board]
    uf2 = args.uf2 or default_uf2
    drive = find_uf2_drive(chip)
    if not drive:
        enter_bootloader(args.board)
        drive = wait_for(lambda: find_uf2_drive(chip), timeout=10)
        if not drive:
            sys.exit("Bootloader drive did not appear.")
    print(f"Copying {os.path.basename(uf2)} -> {drive}")
    shutil.copy(uf2, drive)
    port = wait_for(lambda: find_board(args.board), timeout=15)
    print(f"Running {args.board} on {port}" if port else f"Copied; no {args.board} answering yet.")


if __name__ == "__main__":
    main()
