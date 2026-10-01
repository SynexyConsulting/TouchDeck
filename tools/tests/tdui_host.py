"""The device mirror's renderer on the PC, for tests: builds hostui/ (both boards)
with MSVC, and turns the board's STATE/TEXT/CLIPTEXT lines into a ui_state_t
the same way the app does (windows-app/src/TouchDeck.Core/Mirror)."""
import ctypes
import os
import struct
import subprocess
import zlib

import jig_host

ROOT = jig_host.ROOT
BOARDS = {"rp2040": "rp2040-169", "esp32c3": "esp32c3-128", "rp2350": "rp2350-128"}
CLIP_VIEW = 1024


class UiState(ctypes.Structure):
    """ui_state.h, field for field."""
    _fields_ = [(n, ctypes.c_int32) for n in (
        "screen", "sub", "time_s", "helper", "link_ok", "muted", "timer_s", "bt_mode", "bt_avail",
        "bt_state", "bt_ready", "bt_secs_left")] + [("bt_passkey", ctypes.c_uint32)] + [
        (n, ctypes.c_int32) for n in ("clip_len", "clip_state", "paste_pos", "jig_on", "jig_demo", "jig_paused",
                                      "jig_phase", "jig_letter", "jig_scale")] + [
        ("jig_x", ctypes.c_float), ("jig_y", ctypes.c_float), ("jig_next_s", ctypes.c_int32),
        ("jig_up_s", ctypes.c_int32), ("jig_menus", ctypes.c_uint32), ("clip_src", ctypes.c_char * 12),
        ("msg", ctypes.c_char * 40), ("bt_host", ctypes.c_char * 32), ("down_reason", ctypes.c_char * 32),
        ("clip", ctypes.c_char * CLIP_VIEW)]


# STATE key -> ui_state_t field (the app's MirrorState uses the same table).
STATE_FIELDS = {
    "page": "screen", "sub": "sub", "t": "time_s", "pc": "helper", "link": "link_ok", "mute": "muted",
    "timer": "timer_s", "mode": "bt_mode", "bta": "bt_avail", "bts": "bt_state", "btr": "bt_ready",
    "left": "bt_secs_left", "pk": "bt_passkey", "clip": "clip_len", "cst": "clip_state", "ppos": "paste_pos",
    "jig": "jig_on", "demo": "jig_demo", "paused": "jig_paused", "phase": "jig_phase", "scale": "jig_scale",
    "x": "jig_x", "y": "jig_y", "next": "jig_next_s", "up": "jig_up_s", "menus": "jig_menus",
}
TEXT_FIELDS = {"msg": ("msg", 40), "src": ("clip_src", 12), "host": ("bt_host", 32), "down": ("down_reason", 32)}


def unescape(s):
    out, i = bytearray(), 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            n = s[i + 1]
            if n == "x" and i + 3 < len(s):
                out.append(int(s[i + 2:i + 4], 16)); i += 4; continue
            out += {"n": b"\n", "r": b"\r", "t": b"\t"}.get(n, n.encode()); i += 2; continue
        out += c.encode(); i += 1
    return bytes(out)


class Renderer:
    def __init__(self, dll):
        self.lib = ctypes.CDLL(dll)
        self.lib.tdui_letter_index.argtypes = [ctypes.c_char]
        self.lib.tdui_render.argtypes = [ctypes.POINTER(UiState), ctypes.POINTER(ctypes.c_uint16)]
        self.lib.tdui_state_line.argtypes = [ctypes.POINTER(UiState), ctypes.c_char_p, ctypes.c_int]
        self.lib.tdui_clip_line.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
        self.w, self.h = self.lib.tdui_width(), self.lib.tdui_height()

    def render(self, st):
        buf = (ctypes.c_uint16 * (self.w * self.h))()
        self.lib.tdui_render(ctypes.byref(st), buf)
        return bytes(buf)                            # little-endian RGB565, row by row

    def crc(self, frame, x=0, y=0, w=None, h=None):
        """CRC-32 of a region, as the firmware's FBCRC computes it."""
        w, h = w or self.w, h or self.h
        rows = b"".join(frame[((y + r) * self.w + x) * 2:((y + r) * self.w + x + w) * 2] for r in range(h))
        return zlib.crc32(rows)

    def state_line(self, st):
        out = ctypes.create_string_buffer(512)
        self.lib.tdui_state_line(ctypes.byref(st), out, 512)
        return out.value.decode()

    def clip_line(self, data):
        out = ctypes.create_string_buffer(10 + 4 * CLIP_VIEW)
        self.lib.tdui_clip_line(data, len(data), out, len(out))
        return out.value.decode()

    def apply(self, st, line):
        """Apply one board line to st (as the app does)."""
        if line.startswith("STATE "):
            f = dict(kv.split("=", 1) for kv in line.split()[1:] if "=" in kv)
            for k, name in STATE_FIELDS.items():
                if k in f:
                    setattr(st, name, float(f[k]) if name in ("jig_x", "jig_y") else int(f[k]))
            if "letter" in f and len(f["letter"]) == 1:
                st.jig_letter = max(0, self.lib.tdui_letter_index(f["letter"].encode()))
        elif line.startswith("TEXT "):
            parts = line.split(" ", 2)
            if len(parts) >= 2 and parts[1] in TEXT_FIELDS:
                name, size = TEXT_FIELDS[parts[1]]
                setattr(st, name, (parts[2] if len(parts) > 2 else "").encode()[:size - 1])
        elif line.startswith("CLIPTEXT"):
            data = unescape(line[9:])[:CLIP_VIEW]
            ctypes.memmove(ctypes.addressof(st) + UiState.clip.offset, data, len(data))
            ctypes.memset(ctypes.addressof(st) + UiState.clip.offset + len(data), 0, CLIP_VIEW - len(data))
        return st


def build(tmp):
    """{board: Renderer} for every BOARDS entry, or None when MSVC isn't installed."""
    if not jig_host.vcvars():
        return None
    out = os.path.join(tmp, "tdui")
    r = subprocess.run(["cmd", "/c", os.path.join(ROOT, "hostui", "build.bat"), out], capture_output=True, text=True)
    assert r.returncode == 0, r.stdout + r.stderr
    return {b: Renderer(os.path.join(out, f"tdui_{b}.dll")) for b in BOARDS}


def to_png(frame, w, h, path):
    """RGB565 frame -> PNG (for looking at a render)."""
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            (p,) = struct.unpack_from("<H", frame, (y * w + x) * 2)
            raw += bytes(((p >> 11) * 255 // 31, ((p >> 5) & 63) * 255 // 63, (p & 31) * 255 // 31))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw))) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)
