"""The jiggler's menu event (src/jig_menu.c, identical in esp32c3/src): what it sends, in order, and
the waits between, for each setting. Compiled for the PC with MSVC and driven through ctypes."""
import ctypes
import filecmp
import os
import subprocess

import pytest

import jig_host

ROOT = jig_host.ROOT
JIG_MOVING, JIG_STOP, JIG_CLICK_DOWN, JIG_MENU_OPEN, JIG_ESC_DOWN, JIG_RESUME = range(6)
NONE, RIGHT_DOWN, RIGHT_UP, KEY_DOWN, KEY_UP, SWITCH = range(6)
NAMES = {RIGHT_DOWN: "RD", RIGHT_UP: "RU", KEY_DOWN: "KD", KEY_UP: "KU", SWITCH: "SW"}


class Cfg(ctypes.Structure):
    _fields_ = [("menu_on", ctypes.c_uint8), ("key_f15", ctypes.c_uint8),
                ("open_s", ctypes.c_uint8), ("pause_s", ctypes.c_uint8)]


class Step(ctypes.Structure):
    _fields_ = [("action", ctypes.c_uint8), ("next_phase", ctypes.c_uint8), ("wait_ms", ctypes.c_uint32)]


@pytest.fixture(scope="module")
def lib(tmp_path_factory):
    vc = jig_host.vcvars()
    if not vc:
        pytest.skip("MSVC not installed")
    tmp = str(tmp_path_factory.mktemp("jm"))
    src = os.path.join(ROOT, "src")
    dll = os.path.join(tmp, "jm.dll")
    bat = os.path.join(tmp, "b.bat")
    with open(bat, "w") as f:
        f.write(f'@call "{vc}" >nul\r\ncd /d "{tmp}"\r\n'
                f'cl /nologo /LD /O2 /I"{src}" "{os.path.join(src, "jig_menu.c")}" /link /EXPORT:jmenu_step '
                f'/EXPORT:jmenu_clamp /EXPORT:jmenu_key /OUT:"{dll}"\r\n')
    r = subprocess.run(["cmd", "/c", bat], capture_output=True, text=True)
    assert r.returncode == 0, r.stdout + r.stderr
    l = ctypes.CDLL(dll)
    l.jmenu_step.restype = Step
    l.jmenu_step.argtypes = [ctypes.c_int, ctypes.POINTER(Cfg), ctypes.c_uint32]
    l.jmenu_clamp.argtypes = [ctypes.POINTER(Cfg)]
    l.jmenu_key.restype = ctypes.c_uint8
    l.jmenu_key.argtypes = [ctypes.POINTER(Cfg)]
    return l


def event(lib, cfg, rnd=12345):
    """[(action, wait_ms)] from JIG_STOP until the event hands back to JIG_MOVING."""
    out, phase = [], JIG_STOP
    for _ in range(10):
        s = lib.jmenu_step(phase, ctypes.byref(cfg), rnd)
        out.append((NAMES.get(s.action, s.action), s.wait_ms))
        phase = s.next_phase
        if phase == JIG_MOVING:
            return out
    raise AssertionError(f"event never finished: {out}")


def test_defaults_right_click_hold_two_seconds_then_esc(lib):
    cfg = Cfg(1, 0, 2, 0)
    ev = event(lib, cfg)
    assert [a for a, _ in ev] == ["RD", "RU", "KD", "KU", "SW"]
    waits = dict(zip([a for a, _ in ev], [w for _, w in ev]))
    assert 60 <= waits["RD"] <= 120           # button held briefly
    assert waits["RU"] == 2000                # the menu stays open exactly "Menu open"
    assert 50 <= waits["KD"] <= 90            # key press length
    assert waits["KU"] == 300                 # pause 0: just the settle before the new letter
    assert lib.jmenu_key(ctypes.byref(cfg)) == 0x29   # ESC


def test_menu_off_sends_only_the_key(lib):
    cfg = Cfg(0, 1, 2, 0)
    assert [a for a, _ in event(lib, cfg)] == ["KD", "KU", "SW"]
    assert lib.jmenu_key(ctypes.byref(cfg)) == 0x6A   # F15


def test_pause_comes_after_the_key_and_before_the_new_letter(lib):
    ev = event(lib, Cfg(1, 0, 2, 5))
    assert ev[3] == ("KU", 5300)
    assert ev[4][0] == "SW"


def test_open_zero_goes_straight_to_the_key(lib):
    ev = event(lib, Cfg(1, 0, 0, 0))
    assert ev[1] == ("RU", 0) and ev[2][0] == "KD"


def test_long_values_and_flags_are_clamped(lib):
    cfg = Cfg(7, 9, 99, 200)
    lib.jmenu_clamp(ctypes.byref(cfg))
    assert (cfg.menu_on, cfg.key_f15, cfg.open_s, cfg.pause_s) == (1, 1, 60, 60)


def test_random_jitter_stays_in_range(lib):
    for rnd in (0, 1, 0xFFFFFFFF, 0x80000000, 77777):
        ev = dict(event(lib, Cfg(1, 0, 2, 0), rnd))
        assert 60 <= ev["RD"] <= 120 and 50 <= ev["KD"] <= 90


def test_engine_is_identical_in_both_trees():
    for name in ("jig_menu.c", "jig_menu.h"):
        assert filecmp.cmp(os.path.join(ROOT, "src", name), os.path.join(ROOT, "esp32c3", "src", name), shallow=False)
