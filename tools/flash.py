"""Flash build/watch.uf2 without touching the BOOT button.

Gets the board into its UF2 bootloader by whichever route is available:
  1. already in bootloader  -> just copy
  2. Touch Deck firmware    -> send BOOT over its serial port
  3. stock SDK firmware     -> picotool reboot -u
Close clip_helper.py first; it holds the serial port.
"""
import os
import shutil
import subprocess
import sys

import serial

from touchdeck import SDK_PID, SDK_VID, find_port, find_uf2_drive, wait_for

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UF2 = os.path.join(ROOT, "build", "watch.uf2")
PICOTOOL = os.path.expanduser(r"~\.pico-sdk\picotool\2.1.1\picotool\picotool.exe")


def enter_bootloader():
    port = find_port()
    if port:
        print(f"Touch Deck on {port}: sending BOOT")
        try:
            with serial.Serial(port, 115200, timeout=1) as s:
                s.write(b"\nBOOT\n")
        except serial.SerialException as e:
            sys.exit(f"Can't open {port} ({e}). Is clip_helper.py running? Stop it and retry.")
        return
    if find_port(SDK_VID, SDK_PID):
        print("Stock SDK firmware: picotool reboot -u")
        subprocess.run([PICOTOOL, "reboot", "-f", "-u"], check=False)
        return
    sys.exit("No board found. Hold BOOT while plugging in, then rerun.")


def main():
    uf2 = sys.argv[1] if len(sys.argv) > 1 else UF2
    drive = find_uf2_drive()
    if not drive:
        enter_bootloader()
        drive = wait_for(find_uf2_drive, timeout=10)
        if not drive:
            sys.exit("Bootloader drive did not appear.")
    print(f"Copying {os.path.basename(uf2)} -> {drive}")
    shutil.copy(uf2, drive)
    port = wait_for(find_port, timeout=10)
    print(f"Running on {port}" if port else "Copied; firmware did not enumerate as Touch Deck (yet).")


if __name__ == "__main__":
    main()
