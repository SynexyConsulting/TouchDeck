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

from inject import Injector
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
        t0 = time.perf_counter()
        import uiautomation as auto
        _timing(f"import uiautomation {_ms(t0)}")
    except ImportError:
        return None
    try:
        t0 = time.perf_counter()
        ctrl = auto.GetFocusedControl()
        _timing(f"focused control {_ms(t0)}: "
                f"{ctrl.ControlTypeName if ctrl else None} '{(ctrl.Name if ctrl else '')[:40]}'")
        for level in range(4):                   # focus is sometimes on a child
            if ctrl is None:
                break
            t0 = time.perf_counter()
            tp = ctrl.GetPattern(auto.PatternId.TextPattern)
            _timing(f"level {level} {ctrl.ControlTypeName} TextPattern={bool(tp)} {_ms(t0)}")
            if tp:
                t0 = time.perf_counter()
                text = "".join(r.GetText(-1) for r in tp.GetSelection())
                _timing(f"selection read {_ms(t0)}: {len(text)} chars")
                return text
            ctrl = ctrl.GetParentControl()
    except Exception as e:                       # UIA errors are app-specific
        print(f"  UIA: {e}")
    return None


# Copy-path timing, printed with each COPY (diagnosing slow selection reads).
_timings = []


def _ms(t0):
    return f"{(time.perf_counter() - t0) * 1000:.0f} ms"


def _timing(line):
    _timings.append(line)


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
    _timings.clear()
    t_all = time.perf_counter()
    sel = selection_via_uia()
    if sel:
        result = sel, "select"
    else:
        t0 = time.perf_counter()
        result = clipboard_text(), "clipbd"
        _timing(f"clipboard read {_ms(t0)}")
    _timing(f"total {_ms(t_all)}")
    for line in _timings:
        print(f"    {line}")
    return result


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


def _byte(text):
    """Parse one hex report byte; anything outside 0..FF is malformed."""
    v = int(text, 16)
    if not 0 <= v <= 0xFF:
        raise ValueError(text)
    return v


def handle_line(line, ser, inj):
    """One line from the board. K/M are input reports for PC output mode."""
    parts = line.split()
    try:
        if line == "COPY":
            print("COPY requested")
            send_clip(ser, *grab_text())
        elif line.startswith("LOG "):
            print(time.strftime("%H:%M:%S ") + f"board: {line[4:]}")
        elif parts[0] == "K" and len(parts) == 3:
            inj.key(_byte(parts[1]), _byte(parts[2]))
        elif parts[0] == "M" and len(parts) == 4:
            dx = max(-127, min(127, int(parts[2])))
            dy = max(-127, min(127, int(parts[3])))
            inj.mouse(_byte(parts[1]), dx, dy)
    except (ValueError, IndexError):
        print(f"  ignored malformed line: {line!r}")


def run(debug=False, dry_run=False):
    inj = Injector(dry_run=dry_run, echo=dry_run)
    while True:
        ser = open_board()
        print(f"Connected on {ser.port}" + ("  (dry run: input is logged, not performed)" if dry_run else ""))
        try:
            ser.write(b"\nHELLO\n")
            send_time(ser)
            last_ping = last_time = last_caps_poll = time.time()
            caps = None                      # forces an initial LEDS
            buf = b""
            while True:
                buf += ser.read(256)
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    handle_line(line.strip().decode(errors="replace"), ser, inj)
                now = time.time()
                if now - last_caps_poll >= 0.25:
                    last_caps_poll = now
                    c = inj.caps_lock()
                    if c != caps:
                        caps = c
                        ser.write(b"LEDS 02\n" if c else b"LEDS 00\n")
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
        finally:
            inj.release_all()                # never leave a key or button held on the PC


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--send", metavar="TEXT", help="push TEXT into the board's clip and exit")
    ap.add_argument("--boot", action="store_true", help="reboot the board into the UF2 bootloader")
    ap.add_argument("--debug", action="store_true", help="poll the board's diagnostics every 2 s")
    ap.add_argument("--dry-run", action="store_true", help="log PC-mode keystrokes/mouse instead of performing them")
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
        run(args.debug, args.dry_run)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
