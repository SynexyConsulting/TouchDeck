"""PC side of the Touch Deck clipboard.

When you tap COPY on the board, this reads the text currently *selected* in the
focused window (Windows UI Automation) and sends it to the board's memory. If
the app doesn't expose its selection, it falls back to the current clipboard.
It never sends keystrokes and never changes the clipboard.

Also keeps the watch set to PC time.

  python clip_helper.py              run (Ctrl+C to quit)
  python clip_helper.py --send TEXT  push TEXT to the board and exit
  python clip_helper.py --boot       reboot the board into its UF2 bootloader
"""
import argparse
import ctypes
import sys
import time
import unicodedata
from ctypes import wintypes

import serial

from touchdeck import find_port, open_serial

CLIP_MAX = 8192
PING_S = 2.0

# Characters the US-layout keyboard can't type, mapped to close equivalents.
ASCII_SUBS = {
    "‘": "'", "’": "'", "‚": "'", "“": '"', "”": '"', "„": '"',
    "–": "-", "—": "-", "−": "-", "…": "...", " ": " ", "•": "*",
    "×": "x", "→": "->", "←": "<-", "«": "<<", "»": ">>",
}


def to_ascii(text):
    out, lost = [], 0
    for ch in text:
        ch = ASCII_SUBS.get(ch, ch)
        if all(c < "\x80" for c in ch):
            out.append(ch)
            continue
        base = "".join(c for c in unicodedata.normalize("NFKD", ch) if c < "\x80")
        if base:
            out.append(base)
        else:
            out.append("?")
            lost += 1
    return "".join(out), lost


# ---------- reading the selection ----------

def selection_via_uia():
    """Selected text of the focused control, '' if none, None if unsupported."""
    try:
        import uiautomation as auto
    except ImportError:
        return None
    try:
        ctrl = auto.GetFocusedControl()
        for _ in range(4):                       # focus is sometimes on a child
            if ctrl is None:
                break
            tp = ctrl.GetPattern(auto.PatternId.TextPattern)
            if tp:
                return "".join(r.GetText(-1) for r in tp.GetSelection())
            ctrl = ctrl.GetParentControl()
    except Exception as e:                       # UIA errors are app-specific
        print(f"  UIA: {e}")
    return None


def clipboard_text():
    user32, kernel32 = ctypes.windll.user32, ctypes.windll.kernel32
    user32.GetClipboardData.restype = wintypes.HANDLE
    kernel32.GlobalLock.argtypes = [wintypes.HGLOBAL]
    kernel32.GlobalLock.restype = wintypes.LPVOID
    kernel32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
    for _ in range(5):                           # another app may hold it briefly
        if user32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        return ""
    try:
        h = user32.GetClipboardData(13)          # CF_UNICODETEXT
        if not h:
            return ""
        p = kernel32.GlobalLock(h)
        try:
            return ctypes.wstring_at(p) if p else ""
        finally:
            kernel32.GlobalUnlock(h)
    finally:
        user32.CloseClipboard()


def grab_text():
    sel = selection_via_uia()
    if sel:
        return sel, "select"
    return clipboard_text(), "clipbd"


# ---------- serial protocol ----------

def send_clip(ser, text, src):
    data, lost = to_ascii(text)
    data = data.encode("ascii")[:CLIP_MAX]
    ser.write(f"CLIP {len(data)} {src}\n".encode() + data)
    note = f", {lost} non-ASCII chars as '?'" if lost else ""
    print(f"  sent {len(data)} chars from {src}{note}")


def send_time(ser):
    ser.write(time.strftime("TIME %H:%M:%S\n").encode())


def open_board(wait=True):
    announced = False
    while True:
        port = find_port()
        if port:
            try:
                return open_serial(port)
            except serial.SerialException as e:
                if not wait:
                    sys.exit(f"Can't open {port}: {e}")
        elif not wait:
            sys.exit("Touch Deck not found.")
        if not announced:
            print("Waiting for Touch Deck...")
            announced = True
        time.sleep(1)


def run(debug=False):
    while True:
        ser = open_board()
        print(f"Connected on {ser.port}")
        try:
            ser.write(b"\nHELLO\n")
            send_time(ser)
            last_ping = last_time = time.time()
            buf = b""
            while True:
                buf += ser.read(256)
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    line = line.strip().decode(errors="replace")
                    if line == "COPY":
                        print("COPY requested")
                        send_clip(ser, *grab_text())
                    elif line.startswith("LOG "):
                        print(time.strftime("%H:%M:%S ") + f"board: {line[4:]}")
                now = time.time()
                if now - last_ping >= PING_S:
                    ser.write(b"DBG\n" if debug else b"PING\n")
                    last_ping = now
                if now - last_time >= 3600:
                    send_time(ser)
                    last_time = now
        except (serial.SerialException, OSError):
            print("Disconnected")
            ser.close()
            time.sleep(1)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--send", metavar="TEXT", help="push TEXT into the board's clip and exit")
    ap.add_argument("--boot", action="store_true", help="reboot the board into the UF2 bootloader")
    ap.add_argument("--debug", action="store_true", help="poll the board's diagnostics every 2 s")
    args = ap.parse_args()

    if args.send is not None or args.boot:
        with open_board(wait=False) as ser:
            ser.write(b"\nHELLO\n")
            if args.send is not None:
                send_clip(ser, args.send, "cli")
            if args.boot:
                ser.write(b"BOOT\n")
            ser.flush()
            time.sleep(0.3)
        return
    try:
        run(args.debug)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
