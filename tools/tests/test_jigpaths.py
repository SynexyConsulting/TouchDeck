import filecmp
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ctypes
import pytest
import jigpaths
import jig_host

LETTERS = jigpaths.letters()


def test_letter_set_and_flags():
    assert [n for n, *_ in LETTERS] == list("OWMNZXCVHJLBGD")
    assert {n for n, _, closed, _ in LETTERS if closed} == {"O", "B", "D"}


@pytest.mark.parametrize("board", jigpaths.BOARDS)
def test_lane_and_dot_fit_the_board(board):
    g = jigpaths.BOARDS[board]
    unit = g["box"] / 1000.0
    # The dot (wander + radius) stays inside the lane walls.
    assert jigpaths.WANDER * unit + g["dot"] <= g["lane"] / 2, "dot would touch the wall"
    # The outer wall stays on screen (and, on the round board, inside the circle).
    pad = g["lane"] / 2 + g["wall"] + 1
    for _, pts, _, _ in LETTERS:
        for x, y in pts:
            sx, sy = g["box_x"] + x * unit, g["box_y"] + y * unit
            assert pad <= sx <= g["w"] - pad and pad <= sy <= g["h"] - pad
            if board == "esp32c3":
                assert math.hypot(sx - 120, sy - 120) + pad <= 116


def test_generated_files_are_current():
    for path, text in jigpaths.outputs().items():
        with open(path, encoding="utf-8") as f:
            assert f.read() == text, f"{path} is stale: run python tools/jigpaths.py"


def test_engine_is_identical_in_both_trees():
    root = jigpaths.ROOT
    for name in ("jig_motion.c", "jig_motion.h"):
        assert filecmp.cmp(os.path.join(root, "src", name), os.path.join(root, "esp32c3", "src", name), shallow=False)


@pytest.fixture(scope="module")
def eng(tmp_path_factory):
    lib = jig_host.build(str(tmp_path_factory.mktemp("jig")))
    if lib is None:
        pytest.skip("MSVC not installed")
    return lib


def dist_to_path(pts, closed, x, y):
    best = 1e9
    for (ax, ay), (bx, by) in jigpaths.segments(pts, closed):
        vx, vy = bx - ax, by - ay
        l2 = vx * vx + vy * vy
        t = 0 if l2 == 0 else max(0, min(1, ((x - ax) * vx + (y - ay) * vy) / l2))
        best = min(best, math.hypot(x - ax - vx * t, y - ay - vy * t))
    return best


def test_dot_stays_in_lane(eng):
    assert eng.shim_count() == len(LETTERS)
    for i, (name, pts, closed, cum) in enumerate(LETTERS):
        assert eng.shim_name(i) == name.encode()
        eng.shim_begin(i)
        for _ in range(3000):                        # 30 s at 10 ms: several laps / bounces
            eng.shim_step(0.01)
            d = dist_to_path(pts, closed, eng.shim_x(), eng.shim_y())
            assert d <= jigpaths.WANDER + 0.5, f"{name}: {d:.1f} units off the centre line"


def test_open_letters_bounce_and_closed_loop(eng):
    for i, (name, pts, closed, cum) in enumerate(LETTERS):
        eng.shim_begin(i)
        far = 0.0
        for _ in range(int(cum[-1] / 920 * 100 * 2.5)):   # ~2.5 path lengths
            eng.shim_step(0.01)
            far = max(far, math.dist((eng.shim_x(), eng.shim_y()), pts[0]))
        assert far > 300, f"{name} never left its start"


def test_switch_glides_without_jumps(eng):
    eng.shim_begin(0)
    for _ in range(137): eng.shim_step(0.01)
    eng.shim_switch(5)
    assert eng.shim_gliding() and eng.shim_letter() == 5
    px, py = eng.shim_x(), eng.shim_y()
    for _ in range(80):                                 # 0.8 s > 0.5 s glide
        eng.shim_step(0.01)
        x, y = eng.shim_x(), eng.shim_y()
        assert math.dist((x, y), (px, py)) < 60        # no teleport (units per 10 ms)
        px, py = x, y
    assert not eng.shim_gliding()


def test_pick_never_repeats(eng):
    for cur in range(len(LETTERS)):
        for r in range(200):
            assert eng.shim_pick(cur, r) != cur
            assert 0 <= eng.shim_pick(cur, r) < len(LETTERS)
    assert {eng.shim_pick(-1, r) for r in range(100)} == set(range(len(LETTERS)))


def test_mouse_stays_bounded(eng):
    rnd = random.Random(7)
    eng.shim_begin(rnd.randrange(len(LETTERS)))
    mx, my = ctypes.c_float(), ctypes.c_float()
    # The dot may wander JIG_WANDER past the box edge, so the reach is (1000 + 2*WANDER) * sqrt(2).
    limit = jigpaths.PX_PER_UNIT * 2.0 * (1000 + 2 * jigpaths.WANDER) * math.sqrt(2) + 1
    for step in range(20000):
        eng.shim_step(0.01)
        if step % 1500 == 1499:
            eng.shim_switch(eng.shim_pick(eng.shim_letter(), rnd.getrandbits(32)))
        eng.shim_mouse(rnd.choice([1.0, 1.5, 2.0]), ctypes.byref(mx), ctypes.byref(my))
        assert math.hypot(mx.value, my.value) <= limit


def test_targets_independent_of_sends(eng):
    """The mouse target depends only on engine state, never on what was sent."""
    eng.shim_begin(3)
    mx, my = ctypes.c_float(), ctypes.c_float()
    for _ in range(50): eng.shim_step(0.01)
    eng.shim_mouse(1.0, ctypes.byref(mx), ctypes.byref(my))
    a = (mx.value, my.value)
    eng.shim_mouse(1.0, ctypes.byref(mx), ctypes.byref(my))
    assert (mx.value, my.value) == a
