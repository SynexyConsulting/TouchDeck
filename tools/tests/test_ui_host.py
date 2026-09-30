"""The device pages compiled for the PC (hostui/): what the app's device mirror
draws. Builds both boards' renderers with MSVC; skips without it."""
import ctypes
import filecmp
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pytest
import jig_host
import tdui_host
from tdui_host import UiState

ROOT = jig_host.ROOT
C_BG = 0x0041           # RGB(7, 9, 13)
PC_AMBER = 0xF507       # RGB(242, 163, 58)
PAGES = {"rp2040": 3, "esp32c3": 3}


@pytest.fixture(scope="module")
def tdui(tmp_path_factory):
    r = tdui_host.build(str(tmp_path_factory.mktemp("tdui")))
    if r is None:
        pytest.skip("MSVC not installed")
    return r


def state(**kw):
    st = UiState()
    st.link_ok = 1
    st.clip_src = b"-"
    for k, v in kw.items():
        setattr(st, k, v)
    return st


def pixel(r, frame, x, y):
    return struct.unpack_from("<H", frame, (y * r.w + x) * 2)[0]


@pytest.mark.parametrize("shared", ["ui_state.h", "ui_sync.h", "ui_sync.c"])
def test_shared_files_identical_in_both_trees(shared):
    assert filecmp.cmp(os.path.join(ROOT, "src", shared), os.path.join(ROOT, "esp32c3", "src", shared), shallow=False)


@pytest.mark.parametrize("shared", ["ui_pages.c", "ui_pages.h", "ui.h", "jig_paths.h"])
def test_round_layout_is_the_esp32_copy(shared):
    """The RP2350 round board (src/round) draws the ESP32-C3's round pages."""
    assert filecmp.cmp(os.path.join(ROOT, "src", "round", shared), os.path.join(ROOT, "esp32c3", "src", shared),
                       shallow=False)


def test_struct_layout_matches_the_c_header(tdui):
    for r in tdui.values():
        assert r.lib.tdui_state_size() == ctypes.sizeof(UiState) == 1248


def test_panel_sizes(tdui):
    assert (tdui["rp2040"].w, tdui["rp2040"].h) == (240, 280)
    assert (tdui["esp32c3"].w, tdui["esp32c3"].h) == (240, 240)


@pytest.mark.parametrize("board", ["rp2040", "esp32c3"])
def test_every_page_renders_differently_and_deterministically(tdui, board):
    r = tdui[board]
    frames = [r.render(state(screen=p)) for p in range(PAGES[board])]
    assert len(set(frames)) == len(frames)
    assert r.render(state(screen=1)) == frames[1]


def test_esp32c3_bluetooth_sub_page(tdui):
    r = tdui["esp32c3"]
    settings = r.render(state(screen=2))
    for bts in range(5):
        assert r.render(state(screen=2, sub=1, bt_state=bts, bt_passkey=123456, bt_host=b"DESK")) != settings


@pytest.mark.parametrize("board", ["rp2040", "esp32c3"])
def test_pages_stay_inside_the_panel_shape(tdui, board):
    """Outside the glass (RP2040: 44 px rounded corners; C3: 240 px circle) stays background."""
    r = tdui[board]
    for p in range(PAGES[board]):
        f = r.render(state(screen=p, clip_len=5, clip=b"hello", jig_on=1))
        for y in range(r.h):
            for x in range(r.w):
                if board == "esp32c3":
                    out = (x + 0.5 - 120) ** 2 + (y + 0.5 - 120) ** 2 > 120.5 ** 2
                else:
                    cx = min(max(x + 0.5, 44), 240 - 44)
                    cy = min(max(y + 0.5, 44), 280 - 44)
                    out = (x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2 > 44.5 ** 2
                if out:
                    assert pixel(r, f, x, y) == C_BG, (p, x, y)


@pytest.mark.parametrize("board,box", [("rp2040", (71, 91, 98)), ("esp32c3", (82, 77, 76))])
def test_jiggler_dot_follows_the_synced_position(tdui, board, box):
    r = tdui[board]
    bx, by, size = box
    jig = 2 if board == "rp2040" else 1
    for x, y in [(0, 0), (500, 500), (1000, 250)]:
        f = r.render(state(screen=jig, jig_demo=1, jig_x=x, jig_y=y))
        sx, sy = int(bx + x * size / 1000), int(by + y * size / 1000)
        assert pixel(r, f, sx, sy) == PC_AMBER
    still = r.render(state(screen=jig, jig_x=500, jig_y=500))          # off: no dot
    assert pixel(r, still, bx + size // 2, by + size // 2) != PC_AMBER


@pytest.mark.parametrize("board", ["rp2040", "esp32c3"])
def test_clip_text_message_and_scale_show(tdui, board):
    r = tdui[board]
    clip, jig = (1, 2) if board == "rp2040" else (0, 1)
    empty = r.render(state(screen=clip))
    text = r.render(state(screen=clip, clip_len=5, clip=b"hello", clip_src=b"select"))
    assert empty != text
    assert r.render(state(screen=clip, clip_len=5, clip=b"hello", clip_src=b"select", msg=b"Copied 5 chars")) != text
    assert r.render(state(screen=jig, jig_scale=0)) != r.render(state(screen=jig, jig_scale=2))
    assert r.render(state(screen=jig, jig_letter=0)) != r.render(state(screen=jig, jig_letter=3))


def test_out_of_range_letter_and_scale_do_not_crash(tdui):
    for r in tdui.values():
        r.render(state(screen=2 if r.h == 280 else 1, jig_letter=999, jig_scale=-4, jig_on=1))


@pytest.mark.parametrize("board", ["rp2040", "esp32c3"])
def test_state_line_round_trips_through_the_app_parser(tdui, board):
    """The C serialiser (what the board sends) and the parser agree on every field."""
    r = tdui[board]
    rnd = random.Random(7)
    for _ in range(50):
        st = state(**{n: rnd.randrange(0, 5) for n in (
            "screen", "sub", "helper", "link_ok", "muted", "bt_mode", "bt_avail", "bt_state", "bt_ready",
            "clip_state", "jig_on", "jig_demo", "jig_paused", "jig_phase", "jig_scale")})
        st.time_s, st.timer_s, st.clip_len, st.paste_pos = (rnd.randrange(86400) for _ in range(4))
        st.jig_x, st.jig_y = rnd.randrange(1001), rnd.randrange(1001)
        st.jig_next_s, st.jig_up_s, st.jig_menus = rnd.randrange(200), rnd.randrange(99999), rnd.randrange(999)
        st.bt_secs_left, st.bt_passkey = rnd.randrange(121), rnd.randrange(1000000)
        st.jig_letter = rnd.randrange(14)
        line = r.state_line(st)
        back = r.apply(state(), line)
        for name, _ in UiState._fields_:
            if name not in ("clip_src", "msg", "bt_host", "down_reason", "clip"):
                assert getattr(back, name) == getattr(st, name), (name, line)


def test_clip_line_escapes_every_byte(tdui):
    r = tdui["rp2040"]
    data = bytes(range(256)) * 3
    line = r.clip_line(data)
    assert "\n" not in line and "\r" not in line and all(0x20 <= ord(c) <= 0x7E for c in line)
    assert tdui_host.unescape(line[9:]) == data[:tdui_host.CLIP_VIEW]


def test_text_lines_apply(tdui):
    r = tdui["esp32c3"]
    st = state()
    for line in ("TEXT msg Copied 5 chars", "TEXT src select", "TEXT host DESKTOP 1", "TEXT down Waiting for Bluetooth"):
        r.apply(st, line)
    assert (st.msg, st.clip_src, st.bt_host, st.down_reason) == (
        b"Copied 5 chars", b"select", b"DESKTOP 1", b"Waiting for Bluetooth")
    r.apply(st, "TEXT msg ")
    assert st.msg == b""
