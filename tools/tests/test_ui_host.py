"""The device pages compiled for the PC (hostui/): what the app's device mirror
draws. Builds both boards' renderers (MSVC on Windows, cc elsewhere); skips without a compiler."""
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
PAGES = {"rp2040": 3, "esp32c3": 4, "rp2350": 3}
JIG = {"rp2040": 2, "esp32c3": 2, "rp2350": 2}      # the Jiggler page; its settings open over it (sub=2)
SUB_JIGSET = 2
COG = {"rp2040": (30, 230), "esp32c3": (52, 172), "rp2350": (52, 172)}
ROW2_Y = {"rp2040": 84 + 2 * 40, "esp32c3": 76 + 2 * 32, "rp2350": 76 + 2 * 32}   # JS_ROW_Y(2)
CLOSE = {"rp2040": (30, 42), "esp32c3": (50, 48), "rp2350": (50, 48)}


@pytest.fixture(scope="module")
def tdui(tmp_path_factory):
    r = tdui_host.build(str(tmp_path_factory.mktemp("tdui")))
    if r is None:
        pytest.skip(jig_host.NO_COMPILER)
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
        assert r.lib.tdui_state_size() == ctypes.sizeof(UiState) == 1264


def test_panel_sizes(tdui):
    assert (tdui["rp2040"].w, tdui["rp2040"].h) == (240, 280)
    assert (tdui["esp32c3"].w, tdui["esp32c3"].h) == (240, 240)
    assert (tdui["rp2350"].w, tdui["rp2350"].h) == (240, 240)


def test_round_boards_show_one_dot_per_page(tdui):
    """Round dots sit 12 px apart around x 120: RP2350 Watch, Clipboard, Jiggler (108, 120, 132);
    ESP32-C3 adds Settings (102, 114, 126, 138)."""
    for board, xs, gaps in (("rp2350", (108, 120, 132), (102, 114, 126, 138)),
                            ("esp32c3", (102, 114, 126, 138), (108, 120, 132))):
        r = tdui[board]
        for page in range(PAGES[board]):
            f = r.render(state(screen=page))
            assert all(pixel(r, f, x, 229) != C_BG for x in xs), (board, page)
            assert all(pixel(r, f, x, 229) == C_BG for x in gaps), (board, page)


def cfg(menu=1, key=0, open_s=2, pause_s=0, **kw):
    return dict(jig_menu_on=menu, jig_key=key, jig_open_s=open_s, jig_pause_s=pause_s, **kw)


@pytest.mark.parametrize("board", ["rp2040", "esp32c3", "rp2350"])
def test_jiggler_settings_page_shows_each_setting(tdui, board):
    """Every control changes the picture: the toggle, the key, both times."""
    r = tdui[board]
    base = r.render(state(screen=JIG[board], sub=SUB_JIGSET, **cfg()))
    for change in (cfg(menu=0), cfg(key=1), cfg(open_s=17), cfg(pause_s=9)):
        assert r.render(state(screen=JIG[board], sub=SUB_JIGSET, **change)) != base, change


@pytest.mark.parametrize("board", ["rp2040", "esp32c3", "rp2350"])
def test_menu_open_row_is_dimmed_when_the_menu_is_off(tdui, board):
    """With the context menu off, changing "Menu open" changes nothing that matters, but it is still
    drawn (dimmed): the two renders differ only in that row's value."""
    r = tdui[board]
    on = r.render(state(screen=JIG[board], sub=SUB_JIGSET, **cfg(menu=1, open_s=5)))
    off = r.render(state(screen=JIG[board], sub=SUB_JIGSET, **cfg(menu=0, open_s=5)))
    assert on != off
    # With the menu off, "Menu open" is still drawn (dimmed): its value changes only that row.
    off17 = r.render(state(screen=JIG[board], sub=SUB_JIGSET, **cfg(menu=0, open_s=17)))
    rows = [y for y in range(r.h) if off[y * r.w * 2:(y + 1) * r.w * 2] != off17[y * r.w * 2:(y + 1) * r.w * 2]]
    assert rows, "the dimmed row's value isn't drawn"
    row_y = ROW2_Y[board]
    assert all(abs(y - row_y) <= 13 for y in rows), (rows[0], rows[-1], row_y)


@pytest.mark.parametrize("board", ["rp2040", "esp32c3", "rp2350"])
def test_cog_opens_the_panel_and_the_panel_has_an_x(tdui, board):
    """The Jiggler page shows a cog (lower left); the settings panel replaces the page and shows an X."""
    r = tdui[board]
    jig = r.render(state(screen=JIG[board], **cfg()))
    panel = r.render(state(screen=JIG[board], sub=SUB_JIGSET, **cfg()))
    assert panel != jig
    cx, cy = COG[board]
    assert any(pixel(r, jig, x, y) != C_BG for x in range(cx - 6, cx + 7) for y in range(cy - 6, cy + 7))
    box = [(x, y) for x in range(cx - 6, cx + 7) for y in range(cy - 6, cy + 7)]
    assert [pixel(r, panel, x, y) for x, y in box] != [pixel(r, jig, x, y) for x, y in box]   # no cog on the panel
    xx, xy = CLOSE[board]
    assert pixel(r, panel, xx, xy) != C_BG                 # the X's crossing point


@pytest.mark.parametrize("board", ["rp2040", "esp32c3", "rp2350"])
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


@pytest.mark.parametrize("board", ["rp2040", "esp32c3", "rp2350"])
def test_pages_stay_inside_the_panel_shape(tdui, board):
    """Outside the glass (RP2040: 44 px rounded corners; C3: 240 px circle) stays background."""
    r = tdui[board]
    frames = [r.render(state(screen=p, clip_len=5, clip=b"hello", jig_on=1)) for p in range(PAGES[board])]
    frames.append(r.render(state(screen=JIG[board], sub=SUB_JIGSET, jig_on=1, **cfg())))   # the Jiggler menu panel
    for p, f in enumerate(frames):
        for y in range(r.h):
            for x in range(r.w):
                if board != "rp2040":
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
    jig = 2                              # every board: Watch, Clipboard, Jiggler
    for x, y in [(0, 0), (500, 500), (1000, 250)]:
        f = r.render(state(screen=jig, jig_demo=1, jig_x=x, jig_y=y))
        sx, sy = int(bx + x * size / 1000), int(by + y * size / 1000)
        assert pixel(r, f, sx, sy) == PC_AMBER
    still = r.render(state(screen=jig, jig_x=500, jig_y=500))          # off: no dot
    assert pixel(r, still, bx + size // 2, by + size // 2) != PC_AMBER


@pytest.mark.parametrize("board", ["rp2040", "esp32c3"])
def test_clip_text_message_and_scale_show(tdui, board):
    r = tdui[board]
    clip, jig = 1, 2                     # every board: Watch, Clipboard, Jiggler
    empty = r.render(state(screen=clip))
    text = r.render(state(screen=clip, clip_len=5, clip=b"hello", clip_src=b"select"))
    assert empty != text
    assert r.render(state(screen=clip, clip_len=5, clip=b"hello", clip_src=b"select", msg=b"Copied 5 chars")) != text
    assert r.render(state(screen=jig, jig_scale=0)) != r.render(state(screen=jig, jig_scale=2))
    assert r.render(state(screen=jig, jig_letter=0)) != r.render(state(screen=jig, jig_letter=3))


def test_out_of_range_letter_and_scale_do_not_crash(tdui):
    for r in tdui.values():
        r.render(state(screen=2, jig_letter=999, jig_scale=-4, jig_on=1))


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


@pytest.mark.parametrize("board", ["esp32c3", "rp2350"])
def test_round_watch_face(tdui, board):
    """Page 0 on round boards is a watch: the hands follow the time, the stopwatch shows,
    and with no time yet (ESP32-C3 before the app's TIME) there are no hands."""
    r = tdui[board]
    a = r.render(state(screen=0, time_s=10 * 3600 + 9 * 60 + 36))
    b = r.render(state(screen=0, time_s=10 * 3600 + 9 * 60 + 37))
    assert a != b                                              # the second hand moved
    assert r.render(state(screen=0, time_s=36000, timer_s=754)) != r.render(state(screen=0, time_s=36000))
    none = r.render(state(screen=0, time_s=-1))
    assert pixel(r, none, 124, 120) != pixel(r, a, 124, 120)   # no red centre cap without a time


@pytest.mark.parametrize("board", ["esp32c3", "rp2350"])
def test_round_watch_regions_cover_the_hands(tdui, board):
    """The partial-redraw boxes (hands at t and t+1, stopwatch) cover every pixel that changes
    between two seconds, so drawing a second ahead into just those boxes is enough."""
    r = tdui[board]
    for t in (0, 15, 3599, 10 * 3600 + 9 * 60 + 36, 43199):
        a = r.render(state(screen=0, time_s=t))
        b = r.render(state(screen=0, time_s=t + 1))
        boxes = [r.lib_rect("hands", t), r.lib_rect("hands", t + 1), r.lib_rect("stopwatch")]
        for y in range(r.h):
            for x in range(r.w):
                if pixel(r, a, x, y) != pixel(r, b, x, y):
                    assert any(bx <= x < bx + bw and by <= y < by + bh for bx, by, bw, bh in boxes), (t, x, y)
