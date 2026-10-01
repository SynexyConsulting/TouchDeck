"""gfx_line must draw exactly what the original full-bounding-box version drew.

Builds src/gfx.c (+ fonts) into a shared library (MSVC on Windows, cc elsewhere) and renders random capsules both
ways, including clipped, degenerate, near-horizontal/vertical and off-screen ones.
"""
import ctypes
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pytest
import jig_host

ROOT = jig_host.ROOT


@pytest.fixture(scope="module")
def gfx(tmp_path_factory):
    tmp = str(tmp_path_factory.mktemp("gfx"))
    src = os.path.join(ROOT, "src")
    shim = os.path.join(ROOT, "tools", "tests", "gfx_shim.c")
    dll = jig_host.build_shared(tmp, "gfx", [shim, os.path.join(src, "aa_fonts.c")], [src])
    if not dll:
        pytest.skip(jig_host.NO_COMPILER)
    lib = ctypes.CDLL(dll)
    for fn in ("t_line", "t_ref_line"):
        getattr(lib, fn).argtypes = [ctypes.c_float] * 5 + [ctypes.c_uint16]
    lib.t_fill.argtypes = [ctypes.c_uint16]
    lib.t_fb.restype = ctypes.POINTER(ctypes.c_uint16)
    return lib


def frame(g):
    n = g.t_w() * g.t_h()
    return bytes(ctypes.string_at(g.t_fb(), n * 2))


def render(g, fn, lines, clip):
    g.t_fill(0x1234)
    if clip:
        g.t_clip(*clip)
    for ln in lines:
        fn(*ln)
    return frame(g)


def cases():
    rnd = random.Random(1)
    out = [
        [(10, 10, 10, 10, 6, 0xFFFF)],                   # a dot (zero-length)
        [(0, 100, 239, 100.3, 3, 0xF800)],               # near-horizontal
        [(120, 0, 120.2, 279, 22, 0x07E0)],              # near-vertical, wide
        [(-50, -40, 300, 330, 18, 0x001F)],              # runs off screen both ends
        [(71, 91, 169, 189, 22, 0xFFE0), (71, 91, 169, 189, 18, 0x0000)],   # lane: wall + inner
    ]
    for _ in range(300):
        out.append([(rnd.uniform(-30, 270), rnd.uniform(-30, 310), rnd.uniform(-30, 270), rnd.uniform(-30, 310),
                     rnd.choice([1.4, 2, 2.4, 4, 6.5, 18, 22]), rnd.getrandbits(16)) for _ in range(3)])
    return out


@pytest.mark.parametrize("clip", [None, (60, 80, 40, 30), (0, 206, 240, 44)])
def test_matches_reference_pixel_for_pixel(gfx, clip):
    for lines in cases():
        want = render(gfx, gfx.t_ref_line, lines, clip)
        got = render(gfx, gfx.t_line, lines, clip)
        assert got == want, f"differs for {lines} clip={clip}"
