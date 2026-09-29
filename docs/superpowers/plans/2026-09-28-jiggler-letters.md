# Jiggler Letter Lanes, Clipboard Trash, Tick-only Mute, App Mirror: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
- The jiggler traces random outlined letters, and the mouse follows the dot proportionally.
- The clipboard gets a trash can.
- Mute silences only the watch tick.
- The Windows app mirrors and controls the jiggler and the board's clip live.
- Everything ships as firmware 1.6.0 (both boards) and app 1.1.0 (MSI).

**Architecture:**
- One Python generator (`tools/jigpaths.py`) owns the 15 letter shapes and per-board lane geometry. It emits C for both firmware trees and C# for the app.
- A shared, host-testable C engine (`jig_motion.c`, identical in both trees) walks a letter: arc-length motion, bounce or loop, in-lane wander, glide between letters, and the mouse target.
- Each board's `jiggler` keeps its right-click/Esc phase machine and feeds the engine. Each `ui` draws lanes with a shared `jig_lane.c`, and redraws only a small dirty box around the dot.
- A `WATCH`/`STATE` line protocol streams state to the app, and `JIG`/`CLIP CLEAR` commands control it.

**Tech Stack:**
- Firmware: Pico SDK 2.1.1 C (RP2040); PlatformIO Arduino-ESP32 + LovyanGFX (ESP32-C3).
- Python 3.9 (`python`) for generators and pytest; MSVC `cl` (VS 2022) to host-test the C engine through ctypes.
- App: C# .NET 8 WPF, xUnit, WiX v5.

**Spec:** `docs/superpowers/specs/2026-09-28-jiggler-letters-design.md`

## Global Constraints

- Firmware version `1.6.0` in `src/version.h` and `esp32c3/src/version.h`. App `<Version>1.1.0</Version>` in `windows-app/Directory.Build.props`.
- Letters, in this order: `O W M N Z X C V H J L B G D`. Closed (loop): O, B, D. All others bounce.
- Square layout (RP2040, 240x280):
  - titles centred at y 42;
  - clip text box 20,58 200x110;
  - buttons y 176 h 44;
  - status line centre y 238;
  - trash at (196,43);
  - scale pill 16,33 46x18; ON/OFF pill 178,33 46x18;
  - letter centre-line box 71,91 size 98; lane 18; wall 2; dot radius 5.5.
- Round layout (ESP32, 240 circle):
  - titles centred at y 42 (the chip ends at y 34; this is the spec's "40", adjusted);
  - clip text box 32,52 176x80;
  - buttons y 140 h 40;
  - status line centre y 196;
  - trash at (178,42);
  - scale pill 10,111 44x18; ON/OFF pill 186,111 44x18;
  - letter centre-line box 82,77 size 76; lane 16; wall 2; dot radius 5.
- Letter-box units: 0..1000 across the centre-line box.
  - Motion: speed 920 units/s ±30% sine drift; wander amplitude 30 units; glide 0.5 s.
  - Mouse: 0.12 px per unit at 1x, times the scale (1.0/1.5/2.0).
  - Mouse anchor: the mouse offset is `0.12 × scale × (dot − start point of the first letter)`. This refines the spec's "box centre" anchor: same bounded-area guarantee, no jump at start.
- `STATE` line format, exactly: `STATE jig=<0|1> letter=<L> scale=<0-2> phase=<n> x=<0-1000> y=<0-1000> clip=<len> paste=<0|1>`.
  - Phase numbers: 0 moving, 1 stop, 2 click, 3 menu open, 4 Esc, 5 resume.
  - The spec's `JIG_GLIDE` is not a separate phase. Gliding happens inside phase 0.
- `STATE` is sent only after `WATCH 1`:
  - immediately when any non-position field changes;
  - position updates at most every 100 ms;
  - once right after `WATCH 1`.
- Mute silences only `buzzer_tick()`. `feedback()` always sounds.
- The trash can is active only when `clip_len > 0` and not pasting. `CLIP CLEAR` obeys the same rule.
- Python commands use `python` (3.9). Fonts regenerate only with `python`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Branch: `feature/jiggler-letters`.

## Review Focus

- **The mouse never wanders off.** Over thousands of steps with random letter switches and scale changes, the mouse offset stays within `0.12 × 2.0 × (1000 + 2×30) × √2` px (about 360 px) of the start. Covered by Task 2 test `test_mouse_stays_bounded`.
- **Failed HID sends don't cause drift.** When `usb_mouse`/`out_mouse` is not ready, the engine keeps advancing and the next accepted report catches up; the remainder is never lost. Covered by Task 2 test `test_targets_independent_of_sends`, plus the accumulated `sent_x/sent_y` in Tasks 3 and 6.
- **The trash or `CLIP CLEAR` can't bite mid-paste.** A tap or command while pasting, or with an empty clip, changes nothing. Covered by the Task 5 board test `test_clip_clear_ignored_while_empty` and the guard in `clip_clear()`.
- **Old firmware and the new app.** No `STATE` after `WATCH 1` means the jiggler card falls back and nothing throws. Also, a `STATE` line with extra unknown fields still parses. Covered by the Task 7 tests `State_with_unknown_fields_still_parses` and `Old_firmware_is_detected_by_missing_state`.
- **The dot stays inside its lane walls.** For every letter and both boards, the dot circle stays inside the lane at every step, including through sharp corners. Covered by Task 2 test `test_dot_stays_in_lane`.

---

### Task 1: Tick-only mute (RP2040)

**Files:**
- Modify: `src/main.c` (`feedback()` and the mute-tap comment)

**Interfaces:**
- Consumes: nothing.
- Produces: `feedback()` always beeps.

- [ ] **Step 1: Change `feedback()` and the mute toggle comment**

```c
static void feedback(void) {
    buzzer_tone(2500, 4, 15);   // touch/button click: always on (mute silences only the watch tick)
}
```

In `on_touch`, the mute branch becomes:

```c
        if (e.x >= MUTE_HIT_X && e.y < MUTE_HIT_Y) {
            app.muted = !app.muted;
            feedback();
            app_redraw();
            settings_save();
        }
```

Also update the `app.h` comment on `muted` to `// silences the once-a-second watch tick`.

- [ ] **Step 2: Build**

Run: `P=~/.pico-sdk; export PATH="$P/ninja/v1.12.1:$P/cmake/v3.31.5/bin:$PATH"; ninja -C build`
Expected: builds `build/watch.uf2` with no warnings.

- [ ] **Step 3: Commit**

```bash
git add src/main.c src/app.h
git commit -m "RP2040: mute silences only the watch tick; touch clicks always sound"
```

---

### Task 2: Letter generator and shared motion engine

**Files:**
- Create: `tools/jigpaths.py`, `tools/tests/test_jigpaths.py`, `tools/tests/jig_host.py` (MSVC build helper), `tools/tests/jig_motion_shim.c`
- Create (generated): `src/jig_paths.h`, `src/jig_paths.c`, `esp32c3/src/jig_paths.h`, `esp32c3/src/jig_paths.c`, `windows-app/src/TouchDeck.Core/Jiggler/JigPaths.g.cs`
- Create (hand-written, identical in both trees): `src/jig_motion.h`, `src/jig_motion.c`, `esp32c3/src/jig_motion.h`, `esp32c3/src/jig_motion.c`

**Interfaces:**
- Produces (C, `jig_paths.h`):

```c
typedef struct {
    char name;
    uint8_t closed;          // 1 = loops, 0 = bounces at the ends
    uint8_t n;               // points
    const int16_t (*pts)[2]; // centre-line points, 0..1000 box units
    const float *cum;        // cumulative length per segment start, nseg+1 entries (last = len)
    float len;
} jig_path_t;
extern const jig_path_t JIG_PATHS[];
#define JIG_PATH_COUNT 15
#define JIG_WANDER 30.f            // box units, max sideways offset inside the lane
#define JIG_PX_PER_UNIT 0.12f      // mouse px per box unit at 1x
// Board geometry (differs per tree):
#define JIG_BOX_X 71
#define JIG_BOX_Y 91
#define JIG_BOX 98
#define JIG_LANE 18.f
#define JIG_WALL 2.f
#define JIG_DOT 5.5f
```

- Produces (C, `jig_motion.h`):

```c
typedef struct {
    int letter;          // index into JIG_PATHS
    float s, dir, t;     // arc position, +1/-1, time (s)
    float nx, ny;        // smoothed lane normal (wander direction)
    float glide;         // <0 not gliding, else 0..1 progress
    float gx, gy;        // glide start
    float ax, ay;        // mouse anchor: start point of the first letter
    float x, y;          // dot, box units
} jig_motion_t;
void jm_begin(jig_motion_t *m, int letter);     // fresh start: anchor = this letter's start point
void jm_switch(jig_motion_t *m, int letter);    // glide from the current dot to letter's start
void jm_step(jig_motion_t *m, float dt);
int  jm_gliding(const jig_motion_t *m);
int  jm_pick(int current, uint32_t rnd);         // random letter != current (current < 0: any)
void jm_mouse(const jig_motion_t *m, float scale, float *mx, float *my);   // px offset from the mouse start
```

- Produces (C#): `TouchDeck.Core.Jiggler.JigPaths.All` (an `IReadOnlyList<JigLetter>`), `JigPaths.Find(char)`, `record JigLetter(char Name, bool Closed, (short X, short Y)[] Points)`.

- [ ] **Step 1: Write the generator** `tools/jigpaths.py`

```python
"""Letter lanes for the jiggler: one source for both firmwares and the Windows app.

    python tools/jigpaths.py        # writes src/, esp32c3/src/ and the app's JigPaths.g.cs

Each letter is a centre-line polyline in a unit box (y down) plus a closed flag.
Closed letters loop; open ones bounce back at their ends. H and X retrace their
middle so every letter is one continuous path. Points are emitted in 0..1000
"box units"; each board maps that box onto its screen (BOARDS below).
"""
import math
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

WANDER = 30.0          # box units: max sideways offset of the dot inside the lane
PX_PER_UNIT = 0.12     # mouse px per box unit at 1x (1000 units ~ 120 px)

# Screen geometry of the centre-line box per board (px): x, y, size, lane, wall, dot radius.
BOARDS = {
    "rp2040": dict(dir="src", box_x=71, box_y=91, box=98, lane=18.0, wall=2.0, dot=5.5, w=240, h=280),
    "esp32c3": dict(dir="esp32c3/src", box_x=82, box_y=77, box=76, lane=16.0, wall=2.0, dot=5.0, w=240, h=240),
}


def arc(cx, cy, rx, ry, a0, a1, n):
    return [(cx + rx * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + ry * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


LETTERS = [   # (name, points in a unit box, closed)
    ("O", arc(.5, .5, .5, .5, 0, 360, 48)[:-1], True),
    ("W", [(0, 0), (.22, 1), (.5, .38), (.78, 1), (1, 0)], False),
    ("M", [(0, 1), (0, 0), (.5, .62), (1, 0), (1, 1)], False),
    ("N", [(0, 1), (0, 0), (1, 1), (1, 0)], False),
    ("Z", [(0, 0), (1, 0), (0, 1), (1, 1)], False),
    ("X", [(0, 0), (1, 1), (.5, .5), (1, 0), (0, 1)], False),
    ("C", arc(.55, .5, .45, .5, -45, -315, 36), False),
    ("V", [(0, 0), (.5, 1), (1, 0)], False),
    ("H", [(0, 0), (0, 1), (0, .5), (1, .5), (1, 0), (1, 1)], False),
    ("J", [(.3, 0), (.75, 0), (.75, .68)] + arc(.45, .68, .3, .32, 0, 180, 16)[1:], False),
    ("L", [(0, 0), (0, 1), (1, 1)], False),
    ("B", [(0, .5), (0, 0), (.55, 0)] + arc(.55, .25, .4, .25, -90, 90, 12)[1:] + [(0, .5), (0, 1), (.6, 1)]
     + arc(.6, .75, .4, .25, 90, -90, 12)[1:], True),
    ("G", arc(.55, .5, .45, .5, -40, -360, 40) + [(.55, .5)], False),
    ("D", [(0, 0), (0, 1), (.45, 1)] + arc(.45, .5, .55, .5, 90, -90, 20)[1:], True),
]


def units(pts):
    """Unit box -> integer box units, clamped into 0..1000."""
    return [(max(0, min(1000, round(x * 1000))), max(0, min(1000, round(y * 1000)))) for x, y in pts]


def segments(pts, closed):
    segs = list(zip(pts, pts[1:]))
    if closed:
        segs.append((pts[-1], pts[0]))
    return segs


def cumulative(pts, closed):
    cum = [0.0]
    for a, b in segments(pts, closed):
        cum.append(cum[-1] + math.dist(a, b))
    return cum


def letters():
    """[(name, int points, closed, cumulative lengths)] in LETTERS order."""
    out = []
    for name, pts, closed in LETTERS:
        p = units(pts)
        out.append((name, p, closed, cumulative(p, closed)))
    return out


def c_header(board):
    g = BOARDS[board]
    return f"""// Generated by tools/jigpaths.py - do not edit. Letter lanes for the jiggler.
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {{
#endif
typedef struct {{
    char name;
    uint8_t closed;          // 1 = loops, 0 = bounces at the ends
    uint8_t n;               // points
    const int16_t (*pts)[2]; // centre-line points, 0..1000 box units
    const float *cum;        // cumulative length at each segment start (+ total at the end)
    float len;
}} jig_path_t;
extern const jig_path_t JIG_PATHS[];
#define JIG_PATH_COUNT {len(LETTERS)}
#define JIG_WANDER {WANDER:.1f}f
#define JIG_PX_PER_UNIT {PX_PER_UNIT}f
// This board's screen geometry for the centre-line box (px).
#define JIG_BOX_X {g['box_x']}
#define JIG_BOX_Y {g['box_y']}
#define JIG_BOX {g['box']}
#define JIG_LANE {g['lane']:.1f}f
#define JIG_WALL {g['wall']:.1f}f
#define JIG_DOT {g['dot']:.1f}f
#ifdef __cplusplus
}}
#endif
"""


def c_source():
    out = ["// Generated by tools/jigpaths.py - do not edit.", '#include "jig_paths.h"', ""]
    for name, p, closed, cum in letters():
        out.append(f"static const int16_t P_{name}[][2] = {{" + ", ".join(f"{{{x},{y}}}" for x, y in p) + "};")
        out.append(f"static const float C_{name}[] = {{" + ", ".join(f"{c:.1f}f" for c in cum) + "};")
    out.append("")
    out.append("const jig_path_t JIG_PATHS[] = {")
    for name, p, closed, cum in letters():
        out.append(f"    {{'{name}', {int(closed)}, {len(p)}, P_{name}, C_{name}, {cum[-1]:.1f}f}},")
    out.append("};")
    return "\n".join(out) + "\n"


def cs_source():
    rows = []
    for name, p, closed, _ in letters():
        pts = ", ".join(f"({x}, {y})" for x, y in p)
        rows.append(f"        new('{name}', {'true' if closed else 'false'}, [{pts}]),")
    return f"""// Generated by tools/jigpaths.py - do not edit.
namespace TouchDeck.Core.Jiggler;

/// <summary>A jiggler letter: centre-line points in 0..1000 box units; closed letters loop.</summary>
public sealed record JigLetter(char Name, bool Closed, (short X, short Y)[] Points);

public static class JigPaths
{{
    public static IReadOnlyList<JigLetter> All {{ get; }} =
    [
{chr(10).join(rows)}
    ];

    public static JigLetter? Find(char name) => All.FirstOrDefault(l => l.Name == name);
}}
"""


def outputs():
    """{path: text} of every generated file."""
    files = {}
    for board, g in BOARDS.items():
        files[os.path.join(ROOT, g["dir"], "jig_paths.h")] = c_header(board)
        files[os.path.join(ROOT, g["dir"], "jig_paths.c")] = c_source()
    files[os.path.join(ROOT, "windows-app", "src", "TouchDeck.Core", "Jiggler", "JigPaths.g.cs")] = cs_source()
    return files


def main():
    for path, text in outputs().items():
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("wrote", os.path.relpath(path, ROOT))


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Write the engine** `src/jig_motion.h` and `src/jig_motion.c`, then copy both byte-for-byte to `esp32c3/src/`

`jig_motion.h`:

```c
// Walks the dot along a jiggler letter (jig_paths.h) and turns it into a mouse
// target. Plain C shared by the RP2040 and ESP32-C3 firmwares (identical files)
// and unit-tested on the PC (tools/tests/test_jigpaths.py).
#pragma once
#include <stdint.h>
#include "jig_paths.h"
#ifdef __cplusplus
extern "C" {
#endif
typedef struct {
    int letter;          // index into JIG_PATHS
    float s, dir, t;     // arc position (box units), +1/-1, running time (s)
    float nx, ny;        // smoothed lane normal: the wander direction
    float glide;         // < 0: on the letter; else 0..1 progress towards its start
    float gx, gy;        // where the glide started
    float ax, ay;        // mouse anchor: start point of the first letter
    float x, y;          // dot position, box units
} jig_motion_t;

void jm_begin(jig_motion_t *m, int letter);
void jm_switch(jig_motion_t *m, int letter);
void jm_step(jig_motion_t *m, float dt);
int jm_gliding(const jig_motion_t *m);
int jm_pick(int current, uint32_t rnd);
void jm_mouse(const jig_motion_t *m, float scale, float *mx, float *my);
#ifdef __cplusplus
}
#endif
```

`jig_motion.c`:

```c
#include <math.h>
#include "jig_motion.h"

#define SPEED   920.f    // box units/s (~90 px/s on the 1.69's 98 px box, ~3.4 s per O)
#define GLIDE_S 0.5f
#define NORMAL_TAU 0.06f // s: how fast the wander direction turns through a corner

static int nseg(const jig_path_t *p) { return p->n - 1 + (p->closed ? 1 : 0); }

// Point at arc position s, and the unit normal of the segment it is on.
static void point_at(const jig_path_t *p, float s, float *x, float *y, float *nx, float *ny) {
    int ns = nseg(p), i = 0;
    while (i < ns - 1 && p->cum[i + 1] < s) i++;
    const int16_t *a = p->pts[i], *b = p->pts[(i + 1) % p->n];
    float seg = p->cum[i + 1] - p->cum[i];
    float u = seg > 0.f ? (s - p->cum[i]) / seg : 0.f;
    if (u < 0.f) u = 0.f; else if (u > 1.f) u = 1.f;
    float dx = (float)(b[0] - a[0]), dy = (float)(b[1] - a[1]), l = sqrtf(dx * dx + dy * dy);
    *x = a[0] + dx * u;
    *y = a[1] + dy * u;
    *nx = l > 0.f ? -dy / l : 0.f;
    *ny = l > 0.f ? dx / l : 0.f;
}

static void start_point(int letter, float *x, float *y) {
    *x = JIG_PATHS[letter].pts[0][0];
    *y = JIG_PATHS[letter].pts[0][1];
}

void jm_begin(jig_motion_t *m, int letter) {
    m->letter = letter;
    m->s = 0.f;
    m->dir = 1.f;
    m->t = 0.f;
    m->glide = -1.f;
    start_point(letter, &m->x, &m->y);
    m->ax = m->x;
    m->ay = m->y;
    float px, py;
    point_at(&JIG_PATHS[letter], 0.f, &px, &py, &m->nx, &m->ny);
}

void jm_switch(jig_motion_t *m, int letter) {
    m->letter = letter;
    m->glide = 0.f;
    m->gx = m->x;
    m->gy = m->y;
}

int jm_gliding(const jig_motion_t *m) { return m->glide >= 0.f; }

// Sideways offset inside the lane: two slow sines, |w| <= JIG_WANDER, 0 at t = 0.
static float wander(float t) {
    return JIG_WANDER * (0.6f * sinf(t * 0.9f) + 0.4f * sinf(t * 2.3f));
}

void jm_step(jig_motion_t *m, float dt) {
    const jig_path_t *p = &JIG_PATHS[m->letter];
    m->t += dt;
    if (m->glide >= 0.f) {                          // straight, eased glide to the new start
        m->glide += dt / GLIDE_S;
        float u = m->glide >= 1.f ? 1.f : m->glide;
        u = u * u * (3.f - 2.f * u);
        float sx, sy;
        start_point(m->letter, &sx, &sy);
        m->x = m->gx + (sx - m->gx) * u;
        m->y = m->gy + (sy - m->gy) * u;
        if (m->glide >= 1.f) {
            m->glide = -1.f;
            m->s = 0.f;
            m->dir = 1.f;
            float px, py;
            point_at(p, 0.f, &px, &py, &m->nx, &m->ny);
            m->t = 0.f;                             // wander restarts at 0: no jump off the start point
        }
        return;
    }
    float v = SPEED * (1.f + 0.3f * sinf(m->t * 0.37f));
    m->s += m->dir * v * dt;
    if (p->closed) {
        while (m->s >= p->len) m->s -= p->len;
        while (m->s < 0.f) m->s += p->len;
    } else if (m->s > p->len) {
        m->s = 2.f * p->len - m->s;
        m->dir = -1.f;
    } else if (m->s < 0.f) {
        m->s = -m->s;
        m->dir = 1.f;
    }
    float px, py, nx, ny;
    point_at(p, m->s, &px, &py, &nx, &ny);
    // Turn the wander direction smoothly through corners, so the dot never jumps.
    float a = dt / (dt + NORMAL_TAU);
    m->nx += (nx - m->nx) * a;
    m->ny += (ny - m->ny) * a;
    float nl = sqrtf(m->nx * m->nx + m->ny * m->ny);
    float w = wander(m->t) / (nl > 1.f ? nl : 1.f);   // never longer than JIG_WANDER
    m->x = px + m->nx * w;
    m->y = py + m->ny * w;
}

int jm_pick(int current, uint32_t rnd) {
    if (current < 0) return (int)(rnd % JIG_PATH_COUNT);
    int i = (int)(rnd % (JIG_PATH_COUNT - 1));
    return i >= current ? i + 1 : i;
}

void jm_mouse(const jig_motion_t *m, float scale, float *mx, float *my) {
    *mx = JIG_PX_PER_UNIT * scale * (m->x - m->ax);
    *my = JIG_PX_PER_UNIT * scale * (m->y - m->ay);
}
```

- [ ] **Step 3: Write the host shim and MSVC build helper**

`tools/tests/jig_motion_shim.c`:

```c
// PC-side test shim: exports the engine through plain functions for ctypes.
#include "jig_motion.h"
#define X __declspec(dllexport)
static jig_motion_t m;
X void shim_begin(int letter) { jm_begin(&m, letter); }
X void shim_switch(int letter) { jm_switch(&m, letter); }
X void shim_step(float dt) { jm_step(&m, dt); }
X int shim_gliding(void) { return jm_gliding(&m); }
X int shim_letter(void) { return m.letter; }
X float shim_x(void) { return m.x; }
X float shim_y(void) { return m.y; }
X int shim_pick(int current, unsigned rnd) { return jm_pick(current, rnd); }
X void shim_mouse(float scale, float *mx, float *my) { jm_mouse(&m, scale, mx, my); }
X int shim_count(void) { return JIG_PATH_COUNT; }
X char shim_name(int i) { return JIG_PATHS[i].name; }
```

`tools/tests/jig_host.py`:

```python
"""Builds src/jig_motion.c + jig_paths.c into a DLL with MSVC for the host tests."""
import ctypes
import glob
import os
import subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def vcvars():
    hits = glob.glob(r"C:\Program Files\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat")
    return hits[0] if hits else None


def build(tmp):
    """Returns a ctypes handle, or None when MSVC isn't installed."""
    vc = vcvars()
    if not vc:
        return None
    src = os.path.join(ROOT, "src")
    shim = os.path.join(ROOT, "tools", "tests", "jig_motion_shim.c")
    dll = os.path.join(tmp, "jig.dll")
    bat = os.path.join(tmp, "build.bat")
    with open(bat, "w") as f:
        f.write(f'@call "{vc}" >nul\r\ncd /d "{tmp}"\r\n'
                f'cl /nologo /LD /O2 /I"{src}" "{shim}" "{src}\\jig_motion.c" "{src}\\jig_paths.c" /Fe:"{dll}"\r\n')
    r = subprocess.run(["cmd", "/c", bat], capture_output=True, text=True)
    assert r.returncode == 0, r.stdout + r.stderr
    lib = ctypes.CDLL(dll)
    for fn in ("shim_x", "shim_y"):
        getattr(lib, fn).restype = ctypes.c_float
    for fn in ("shim_step",):
        getattr(lib, fn).argtypes = [ctypes.c_float]
    lib.shim_mouse.argtypes = [ctypes.c_float, ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_float)]
    lib.shim_pick.argtypes = [ctypes.c_int, ctypes.c_uint]
    lib.shim_name.restype = ctypes.c_char
    return lib
```

- [ ] **Step 4: Write the tests** `tools/tests/test_jigpaths.py`

```python
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
```

- [ ] **Step 5: Run the tests. The engine tests fail until the generated files exist.**

Run: `python -m pytest tools/tests/test_jigpaths.py -q`
Expected: FAIL. `test_generated_files_are_current` fails on missing files, and the engine build fails because `jig_paths.h` doesn't exist.

- [ ] **Step 6: Generate the files and re-run**

Run: `python tools/jigpaths.py && python -m pytest tools/tests/test_jigpaths.py -q`
Expected: all pass. If `test_dot_stays_in_lane` or `test_lane_and_dot_fit_the_board` fails, adjust the letter shape or the board geometry in `jigpaths.py` (never the test limits), then regenerate.

- [ ] **Step 7: Commit**

```bash
git add tools/jigpaths.py tools/tests/test_jigpaths.py tools/tests/jig_host.py tools/tests/jig_motion_shim.c \
        src/jig_paths.* src/jig_motion.* esp32c3/src/jig_paths.* esp32c3/src/jig_motion.* \
        windows-app/src/TouchDeck.Core/Jiggler/JigPaths.g.cs
git commit -m "Jiggler letters: generator, shared motion engine, host tests"
```

---

### Task 3: RP2040 jiggler drives the engine

**Files:**
- Modify: `src/jiggler.c`, `src/app.h`, `src/main.c` (DBG `letter=`), `CMakeLists.txt` (add `src/jig_motion.c src/jig_paths.c`)
- Modify: `src/ui.c`: remove the old `anim_demo` angle spin in core1 (demo motion now runs on core0)

**Interfaces:**
- Consumes: `jig_motion.h` (Task 2).
- Produces: `app.jig_letter` (int index), `app.jig_x`, `app.jig_y` (float box units), updated by core0 every step. Also enum `JIG_MOVING` (renamed from `JIG_CIRCLE`, value 0). Removes `app.jig_angle` and `app.jig_radius`.

- [ ] **Step 1: `app.h`: replace the jiggler fields**

```c
enum { JIG_MOVING, JIG_STOP, JIG_CLICK_DOWN, JIG_MENU_OPEN, JIG_ESC_DOWN, JIG_RESUME };
...
    volatile int jig_letter;        // index into JIG_PATHS (jig_paths.h)
    volatile float jig_x, jig_y;    // dot position in letter-box units (0..1000)
```

Delete `jig_angle` and `jig_radius`. Rename every `JIG_CIRCLE` to `JIG_MOVING` (grep `src/`).

- [ ] **Step 2: rewrite the motion part of `jiggler.c`**

Replace `t`, `angle`, `radius_at`, `circle_step` with:

```c
#include "jig_motion.h"

static jig_motion_t m;
static float sent_x, sent_y;   // mouse offset from the start point we have told the host so far
static float scale = 1.f;      // eases toward JIG_SCALES[app.jig_scale_idx] so a change never jumps
static uint32_t next_ms;

static void publish(void) {
    app.jig_letter = m.letter;
    app.jig_x = m.x;
    app.jig_y = m.y;
}

// Demo (ANIM 1, perf tests): walk the letter on screen without sending HID.
static void demo_step(void) {
    if (now_ms() < next_ms) return;
    next_ms = now_ms() + STEP_MS;
    jm_step(&m, STEP_MS / 1000.f);
    publish();
}

static void move_step(void) {
    jm_step(&m, STEP_MS / 1000.f);
    scale += (JIG_SCALES[app.jig_scale_idx] - scale) * 0.03f;   // ~95% settled after 1 s of 10 ms steps
    float tx, ty;
    jm_mouse(&m, scale, &tx, &ty);
    int dx = (int)lroundf(tx - sent_x);
    int dy = (int)lroundf(ty - sent_y);
    if (dx > 127) dx = 127; else if (dx < -127) dx = -127;
    if (dy > 127) dy = 127; else if (dy < -127) dy = -127;
    publish();
    if ((dx || dy) && !usb_mouse(0, (int8_t)dx, (int8_t)dy)) return;   // next step catches up
    sent_x += dx;
    sent_y += dy;
}
```

`jiggler_set(true)` becomes:

```c
    if (app.jig_on) {
        scale = JIG_SCALES[app.jig_scale_idx];
        jm_begin(&m, jm_pick(-1, get_rand_32()));
        sent_x = sent_y = 0.f;       // the mouse's current spot is the letter's start point
        publish();
        app.jig_phase = JIG_MOVING;
        ...                          // (menus, started_ms, next_ms, schedule_menu as before)
    }
```

In `jiggler_step`:

```c
void jiggler_step(void) {
    if (!app.jig_on) {
        if (app.anim_demo) demo_step();
        return;
    }
    if (app.jig_paused) return;
    ...
    case JIG_MOVING:
        next_ms = now_ms() + STEP_MS;
        if ((int32_t)(now_ms() - app.jig_next_menu_ms) >= 0) set_phase(JIG_STOP, 400, 900);
        else move_step();
        break;
    ...
    case JIG_RESUME:
        app.jig_menus++;
        schedule_menu();
        jm_switch(&m, jm_pick(m.letter, get_rand_32()));   // a new letter after every menu
        publish();
        app_redraw();                                     // draw the new letter
        app.jig_phase = JIG_MOVING;
        next_ms = now_ms();
        break;
```

Add `void jiggler_demo_begin(void)` to `jiggler.h`, called by `ANIM 1` in `usb_io.c` (`if (s[5] == '1') jiggler_demo_begin();`). It runs `jm_begin(&m, jm_pick(-1, get_rand_32())); publish(); app_redraw();`, so a demo started while off walks a real letter.

- [ ] **Step 3: DBG and core1 demo code**

- In `debug_report()` (`main.c`), add `letter=%c` with `JIG_PATHS[app.jig_letter].name`, right after `jscale=`, and `#include "jig_paths.h"`.
- In `ui.c`, delete `if (app.anim_demo && anim_jig) app.jig_angle += 0.12f;`.

- [ ] **Step 4: CMake and build**

In `CMakeLists.txt`, add `src/jig_motion.c` and `src/jig_paths.c` to `add_executable(watch …)`.
Run: `ninja -C build`
Expected: compile errors only in `ui.c` (`jig_angle`), fixed in Task 4. To keep this task green, replace `draw_jig`'s dot line with `gfx_disc(JIG_CX, JIG_CY, 6.f, C_PC);` for now, then build clean.

- [ ] **Step 5: Commit**

```bash
git add src/jiggler.c src/jiggler.h src/app.h src/main.c src/usb_io.c src/ui.c CMakeLists.txt
git commit -m "RP2040 jiggler: letter paths via the shared engine, random letter after each menu"
```

---

### Task 4: RP2040 UI (titles, clipboard trash and relayout, letter-lane jiggler, dirty-box animation)

**Files:**
- Create (identical in both trees): `src/jig_lane.c`, `src/jig_lane.h`, `esp32c3/src/jig_lane.c`, `esp32c3/src/jig_lane.h`
- Modify: `src/icons.c`, `src/icons.h`, `esp32c3/src/icons.c`, `esp32c3/src/icons.h` (`icon_trash`)
- Modify: `src/ui.h`, `src/ui.c`, `src/main.c` (touch handling, `clip_clear`, `jig_cycle_scale`), `CMakeLists.txt`
- Modify: `tools/tests/test_fontgen.py` (labels)

**Interfaces:**
- Consumes: `app.jig_letter`, `app.jig_x`, `app.jig_y` (Task 3); `JIG_*` geometry (Task 2).
- Produces:
  - `void jig_draw_lane(const jig_path_t *p, uint16_t wall_col, uint16_t inner_col)`;
  - `void jig_dot_screen(float bx, float by, float *sx, float *sy)`;
  - `void icon_trash(float cx, float cy, float size, uint16_t col)`;
  - `void clip_clear(void)` (main.c, used by the tap and by `CLIP CLEAR` in Task 5);
  - `void jig_cycle_scale(void)` (main.c).

- [ ] **Step 1: `jig_lane.h` / `jig_lane.c`** (write in `src/`, copy byte-for-byte to `esp32c3/src/`)

```c
// Draws a jiggler letter as an outlined lane (jig_paths.h geometry). Shared by both UIs.
#pragma once
#include <stdint.h>
#include "jig_paths.h"
#ifdef __cplusplus
extern "C" {
#endif
void jig_dot_screen(float bx, float by, float *sx, float *sy);   // box units -> screen px
void jig_draw_lane(const jig_path_t *p, uint16_t wall_col, uint16_t inner_col);
#ifdef __cplusplus
}
#endif
```

```c
#include "jig_lane.h"
#include "gfx.h"

void jig_dot_screen(float bx, float by, float *sx, float *sy) {
    *sx = JIG_BOX_X + bx * (JIG_BOX / 1000.f);
    *sy = JIG_BOX_Y + by * (JIG_BOX / 1000.f);
}

// Two passes of round-capped strokes: the wide one in the wall colour, then the
// lane width in the inside colour. Where strokes cross (X, H) the lanes merge.
static void pass(const jig_path_t *p, float thick, uint16_t col) {
    int ns = p->n - 1 + (p->closed ? 1 : 0);
    for (int i = 0; i < ns; i++) {
        float ax, ay, bx, by;
        jig_dot_screen(p->pts[i][0], p->pts[i][1], &ax, &ay);
        jig_dot_screen(p->pts[(i + 1) % p->n][0], p->pts[(i + 1) % p->n][1], &bx, &by);
        gfx_line(ax, ay, bx, by, thick, col);
    }
}

void jig_draw_lane(const jig_path_t *p, uint16_t wall_col, uint16_t inner_col) {
    pass(p, JIG_LANE + 2.f * JIG_WALL, wall_col);
    pass(p, JIG_LANE, inner_col);
}
```

- [ ] **Step 2: `icon_trash`** in `icons.c`/`icons.h` (both trees, identical)

```c
void icon_trash(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(3, 6, 21, 6);                                  // lid
    seg(9, 6, 9, 3); seg(9, 3, 15, 3); seg(15, 3, 15, 6);   // handle
    seg(6, 9, 7, 21); seg(7, 21, 17, 21); seg(17, 21, 18, 9);   // bin
    seg(10.5f, 11, 10.5f, 18); seg(13.5f, 11, 13.5f, 18);       // ribs
}
```

`icons.h`: add `void icon_trash(float cx, float cy, float size, uint16_t col);`.

- [ ] **Step 3: `ui.h` geometry**

```c
#define TITLE_Y    42          // page titles (centre of capitals)

// Clipboard: text box, then the buttons, then the status line.
#define BTN_Y      176
#define BTN_H      44
#define BTN_COPY_X 20
#define BTN_PASTE_X 122
#define BTN_W      98
#define CLIP_STATUS_Y 238
#define TRASH_CX   196
#define TRASH_CY   43
#define TRASH_HIT  18          // half-size of the square tap target

// Jiggler: pills either side of the title; the letter lane below (jig_paths.h).
#define PILL_Y     33
#define PILL_H     18
#define PILL_W     46
#define SCALE_PILL_X 16
#define ONOFF_PILL_X 178
#define PILL_PAD   6            // extra tap margin around the pills
#define JIG_ZONE_PAD 10         // letter box + this = the ON/OFF tap zone
```

Delete `JIG_CX`, `JIG_CY` and `JIG_R`.

- [ ] **Step 4: `ui.c`: titles, `draw_clip`, `draw_jig`**

Titles: replace `text_c(LCD_W / 2, 48, "Clipboard", …)` and the Jiggler equivalent with `TITLE_Y`.

`draw_clip` changes:
- `CY0 = 58`;
- the trash, drawn after the title: `icon_trash(TRASH_CX, TRASH_CY, 18.f, len && !pasting ? C_TEXT : C_FAINT);` (compute `pasting` before the title);
- the status/progress block moved below the buttons:

```c
    button(BTN_COPY_X, BTN_Y, BTN_W, BTN_H, C_SURF2, C_TEXT,
           app.clip_state == CLIP_COPYING ? "..." : "Copy", icon_copy, NULL);
    if (pasting) button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, C_BAD, C_BG, "Stop", NULL, NULL);
    else button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, len ? C_PC : C_SURF2, len ? C_BG : C_FAINT,
                "Paste", NULL, icon_arrow_right);
    if (pasting && len) {
        gfx_rrect(40, CLIP_STATUS_Y - 2, 160, 4, 2.f, C_SURF2);
        gfx_rrect(40, CLIP_STATUS_Y - 2, 160 * app.paste_pos / len + 1, 4, 2.f, C_PC);
    } else {
        text_c(LCD_W / 2, CLIP_STATUS_Y, msg_or(info, buf, sizeof buf), &font_body, C_DIM, 0);
    }
```

`draw_jig` replaces the ring, disc and inner texts with:

```c
static void jig_pill(int x, const char *label, uint16_t bg, uint16_t fg, uint16_t ring) {
    pill(x, PILL_Y, PILL_W, PILL_H, ring);
    if (ring != bg) pill(x + 1, PILL_Y + 1, PILL_W - 2, PILL_H - 2, bg);
    text_c(x + PILL_W / 2, PILL_Y + PILL_H / 2, label, &font_caps, fg, 1);
}

static void draw_jig(void) {
    char s[40], buf[40], sc[8];
    bool on = app.jig_on, live = on || app.anim_demo;
    text_c(LCD_W / 2, TITLE_Y, "Jiggler", &font_title, C_TEXT, 0);
    snprintf(sc, sizeof sc, "%.1fX", (double)JIG_SCALES[app.jig_scale_idx]);
    jig_pill(SCALE_PILL_X, sc, C_SURF2, C_PC, C_SURF2);
    jig_pill(ONOFF_PILL_X, on ? "ON" : "OFF", on ? C_PC_TINT : C_SURF2, on ? C_PC : C_DIM, on ? C_PC : C_SURF2);
    jig_draw_lane(&JIG_PATHS[app.jig_letter], live ? C_PC : C_SURF2, C_INNER);
    if (live) {
        float x, y;
        jig_dot_screen(app.jig_x, app.jig_y, &x, &y);
        gfx_disc(x, y, JIG_DOT, C_PC);
    }
    ... (status line at 220 and stats at 238: unchanged)
}
```

- [ ] **Step 5: `ui.c`: dirty-box animation**

Replace `JIG_ANIM` with a status rectangle plus the dot's dirty box:

```c
static const rect_t JIG_STATUS = {0, 206, LCD_W, 44};
static const rect_t CLIP_ANIM[] = {{0, CLIP_STATUS_Y - 10, LCD_W, 20}};

// Screen box around the dot at box position (bx, by), wide enough for its anti-aliased edge.
static rect_t dot_rect(float bx, float by) {
    float x, y;
    jig_dot_screen(bx, by, &x, &y);
    int r = (int)JIG_DOT + 2;
    return (rect_t){(int)x - r, (int)y - r, 2 * r + 1, 2 * r + 1};
}
```

In `ui_core1_main`:
- keep `rect_t prev_dot` (set to `dot_rect(app.jig_x, app.jig_y)` on every full redraw of the jiggler page);
- the partial branch for `anim_jig` becomes:

```c
            float jx = app.jig_x, jy = app.jig_y;               // one consistent sample
            rect_t now = dot_rect(jx, jy);
            rect_t r[2] = {clamp_to_screen(rect_union(prev_dot, now)), JIG_STATUS};
            for (int i = 0; i < 2; i++) {
                gfx_set_clip(r[i].x, r[i].y, r[i].w, r[i].h);
                draw_page(screen, app.time_s);
            }
            gfx_clip_reset();
            t_push = time_us_64();
            for (int i = 0; i < 2; i++) lcd_push_rect(fb, r[i].x, r[i].y, r[i].w, r[i].h);
            prev_dot = now;
```

`draw_jig` reads `app.jig_x/y` itself, so the drawn dot can be a sample newer than `now`. Pass the sampled position instead: add `static float jig_draw_x, jig_draw_y;`, set it before `draw_page`, and have `draw_jig` use them. Full frames set them from `app` too.

- [ ] **Step 6: `main.c`: touch, `clip_clear`, `jig_cycle_scale`**

```c
void clip_clear(void) {
    if (app.clip_len == 0 || app.clip_state == CLIP_PASTING) return;
    mutex_enter_blocking(&clip_mtx);
    app.clip_len = 0;
    app.clip[0] = 0;
    strcpy(app.clip_src, "-");
    mutex_exit(&clip_mtx);
    app_message("Cleared");
    app_redraw();
}

void jig_cycle_scale(void) {
    app.jig_scale_idx = (app.jig_scale_idx + 1) % JIG_SCALE_COUNT;   // 1x -> 1.5x -> 2x -> 1x
    app_redraw();
    settings_save();
}

static bool near_box(const touch_event_t *e, int x, int y, int w, int h, int pad) {
    return in_rect(e, x - pad, y - pad, w + 2 * pad, h + 2 * pad);
}
```

In `on_touch`, the clipboard branch gets the trash before Copy/Paste:

```c
        if (near_box(&e, TRASH_CX - TRASH_HIT, TRASH_CY - TRASH_HIT, 2 * TRASH_HIT, 2 * TRASH_HIT, 0)) {
            if (app.clip_len && app.clip_state != CLIP_PASTING) { feedback(); clip_clear(); }
            return;
        }
```

The jiggler branch becomes:

```c
    } else if (app.screen == SCR_JIG) {
        const int pad = (int)(JIG_LANE / 2 + JIG_WALL) + JIG_ZONE_PAD;
        if (near_box(&e, SCALE_PILL_X, PILL_Y, PILL_W, PILL_H, PILL_PAD)) {
            feedback();
            jig_cycle_scale();
        } else if (near_box(&e, ONOFF_PILL_X, PILL_Y, PILL_W, PILL_H, PILL_PAD) ||
                   near_box(&e, JIG_BOX_X, JIG_BOX_Y, JIG_BOX, JIG_BOX, pad)) {
            feedback();
            jiggler_toggle();
            settings_save();
        }
    }
```

`on_button` on the jiggler page calls `feedback(); jig_cycle_scale();`. Declare `clip_clear` and `jig_cycle_scale` in `app.h`.

Update the scripted jiggler taps in the tests to hit the new zone at 120,140.

- [ ] **Step 7: font label tests**

In `tools/tests/test_fontgen.py` `LABELS`:
- remove `("font_caps", "TAP TO START", 80, 1)`;
- add `("font_caps", "OFF", 46 - 12, 1)`, `("font_caps", "2.0X", 44 - 10, 1)` and `("font_body", "Cleared", 190, 0)`.

Run: `python -m pytest tools/tests/test_fontgen.py -q`. Expected: pass.

- [ ] **Step 8: Build, flash, look**

Run:
- `ninja -C build && python tools/flash.py`: flashes over COM6 (quit the Windows app first).
- `python tools/perf_rp2040.py`: expect jiggler `drawmax` under 25 ms for animation frames.

Then look at the device: clipboard with and without text (use `python tools/clip_helper.py --send "hello"`), trash clears it; jiggler pills; `ANIM 1` walks a letter.

- [ ] **Step 9: Commit**

```bash
git add src/ esp32c3/src/icons.* esp32c3/src/jig_lane.* CMakeLists.txt tools/tests/test_fontgen.py
git commit -m "RP2040 UI: higher titles, clipboard trash + relayout, letter-lane jiggler with pills, dot-only redraw"
```

---

### Task 5: RP2040 protocol (WATCH/STATE, JIG, CLIP CLEAR)

**Files:**
- Modify: `src/usb_io.c` (commands, `usb_state_poll`, header comment), `src/usb_io.h`, `src/main.c` (call `usb_state_poll()` in the loop)
- Test: `tools/tests/test_board_rp2040.py`

**Interfaces:**
- Consumes: `clip_clear()`, `jig_cycle_scale()`, `jiggler_set()`, `app.jig_*`.
- Produces: the wire protocol in Global Constraints. The C# side (Task 7) parses it.

- [ ] **Step 1: Write the failing board tests** (append to `test_board_rp2040.py`)

```python
def state_lines(board, secs):
    board.take(); board.pump(secs)
    return [l for l in board.take() if l.startswith("STATE ")]

def fields(line):
    return dict(kv.split("=", 1) for kv in line.split()[1:])

def test_watch_streams_state(board):
    board.send("WATCH 1")
    lines = state_lines(board, 0.5)
    assert lines, "no STATE after WATCH 1"
    f = fields(lines[-1])
    assert set(f) >= {"jig", "letter", "scale", "phase", "x", "y", "clip", "paste"}
    assert f["letter"] in "OWMNZXCVHJLBGD"
    board.send("WATCH 0"); board.pump(0.2)
    assert not state_lines(board, 0.6), "STATE kept coming after WATCH 0"

def test_anim_demo_moves_the_dot_in_state(board):
    board.send("WATCH 1"); board.send("ANIM 1")
    try:
        pts = {(fields(l)["x"], fields(l)["y"]) for l in state_lines(board, 1.2)}
        assert len(pts) >= 5, "dot position did not stream"
    finally:
        board.send("ANIM 0"); board.send("WATCH 0"); board.pump(0.2)

def test_jig_scale_command(board):
    board.send("WATCH 1")
    start = int(fields(state_lines(board, 0.4)[-1])["scale"])
    try:
        for want in (2, 0, 1):
            board.send(f"JIG SCALE {want}"); board.pump(0.2)
            assert board.field("jscale") == ["1.0", "1.5", "2.0"][want]
    finally:
        board.send(f"JIG SCALE {start}"); board.send("WATCH 0"); board.pump(0.2)

def test_jig_on_off_command(board):
    """Moves the real mouse a few px for ~0.3 s (the RP2040 is a USB mouse)."""
    was = board.field("jig")
    try:
        board.send("JIG ON"); board.pump(0.3)
        assert board.field("jig") == "1"
        board.send("JIG OFF"); board.pump(0.2)
        assert board.field("jig") == "0"
    finally:
        board.send("JIG ON" if was == "1" else "JIG OFF"); board.pump(0.2)

def test_clip_clear_ignored_while_empty(board):
    board.send("WATCH 1")
    board.send("CLIP 5 test"); board.s.write(b"hello"); board.pump(0.3)
    assert fields(state_lines(board, 0.3)[-1])["clip"] == "5"
    board.send("CLIP CLEAR"); board.pump(0.3)
    assert fields(state_lines(board, 0.3)[-1])["clip"] == "0"
    board.send("CLIP CLEAR"); board.pump(0.3)          # empty: a no-op, no error
    assert fields(state_lines(board, 0.3)[-1])["clip"] == "0"
    board.send("WATCH 0"); board.pump(0.2)
```

`state_lines` after `WATCH 1` relies on the firmware re-sending `STATE` on any field change, and once right after `WATCH 1`. Where a test needs the latest state without a change, send `WATCH 1` again: the firmware resets its "last sent" and reports once.

- [ ] **Step 2: Run them to confirm they fail**

Run: `python -m pytest tools/tests/test_board_rp2040.py -q -k "watch or state or jig_ or clip_clear"`
Expected: FAIL ("no STATE after WATCH 1").

- [ ] **Step 3: Implement in `usb_io.c`**

Header comment additions:

```c
//               WATCH 1|0              start/stop STATE reports (app mirror)
//               JIG ON|OFF, JIG SCALE n   jiggler on/off, scale index 0-2 (saved)
//               CLIP CLEAR             empty the clip (ignored while pasting)
// board -> PC:  STATE jig= letter= scale= phase= x= y= clip= paste=   (after WATCH 1)
```

Commands, in `handle_line` before `BOOT`:

```c
    } else if (!strcmp(s, "WATCH 1") || !strcmp(s, "WATCH 0")) {
        watching = s[6] == '1';
        state_head[0] = 0;                    // report once right away
    } else if (!strcmp(s, "JIG ON") || !strcmp(s, "JIG OFF")) {
        jiggler_set(s[5] == 'N');
        settings_save();
    } else if (!strncmp(s, "JIG SCALE ", 10)) {
        int n = s[10] - '0';
        if (n >= 0 && n < JIG_SCALE_COUNT && !s[11]) {
            app.jig_scale_idx = n;
            app_redraw();
            settings_save();
        }
    } else if (!strcmp(s, "CLIP CLEAR")) {
        clip_clear();
```

The reporter:

```c
static bool watching;
static char state_head[64];          // STATE without x/y, as last sent
static uint32_t state_ms;

void usb_state_poll(void) {
    if (!watching || !tud_cdc_connected()) return;
    char head[64], line[96];
    snprintf(head, sizeof head, "STATE jig=%d letter=%c scale=%d phase=%d", app.jig_on ? 1 : 0,
             JIG_PATHS[app.jig_letter].name, app.jig_scale_idx, app.jig_phase);
    bool moving = (app.jig_on && !app.jig_paused) || app.anim_demo;
    bool head_changed = strcmp(head, state_head) != 0;
    static int last_clip = -1, last_paste = -1;
    int paste = app.clip_state == CLIP_PASTING;
    bool other_changed = app.clip_len != last_clip || paste != last_paste;
    if (!head_changed && !other_changed && !(moving && now_ms() - state_ms >= 100)) return;
    snprintf(line, sizeof line, "%s x=%d y=%d clip=%d paste=%d", head, (int)app.jig_x, (int)app.jig_y,
             app.clip_len, paste);
    usb_send_line(line);
    strcpy(state_head, head);
    last_clip = app.clip_len;
    last_paste = paste;
    state_ms = now_ms();
}
```

`usb_io.h`: `void usb_state_poll(void);`. `main.c`: call it right after `usb_io_poll();` in the loop. Includes: `jig_paths.h`, `jiggler.h`, `settings.h`.

- [ ] **Step 4: Build, flash, run the board tests**

Run: `ninja -C build && python tools/flash.py && python -m pytest tools/tests -q`
Expected: all pass. The ESP32 tests skip. The RP2040 tests need COM6 free, so quit the app.

- [ ] **Step 5: Commit**

```bash
git add src/usb_io.c src/usb_io.h src/main.c tools/tests/test_board_rp2040.py
git commit -m "RP2040 protocol: WATCH/STATE mirror, JIG ON/OFF/SCALE, CLIP CLEAR"
```

---

### Task 6: ESP32-C3 (the same features on the round board)

**Files:**
- Modify: `esp32c3/src/app.h`, `jiggler.cpp`, `jiggler.h`, `ui.cpp`, `ui.h`, `main.cpp`, `link.cpp`, `display.cpp`, `display.h`
- Test: `tools/tests/test_board_pc_mode.py` (ESP32 `WATCH`/`STATE`/`JIG`/`CLIP CLEAR`, skips without the board)

**Interfaces:**
- Consumes: the shared `jig_motion.*`, `jig_paths.*`, `jig_lane.*`, `icon_trash` (already in `esp32c3/src` from Tasks 2 and 4).
- Produces: the same protocol as Task 5. Also `display_push_rect(int x, int y, int w, int h)` and `Preferences` key `jscale`.

- [ ] **Step 1: `app.h`**
  - Add `#define JIG_SCALE_COUNT 3` and `static const float JIG_SCALES[] = {1.0f, 1.5f, 2.0f};`.
  - Add the fields `volatile int jig_scale_idx; volatile int jig_letter; volatile float jig_x, jig_y; volatile bool anim_demo;`.
  - Remove `jig_angle` and `jig_radius`.
  - Rename `JIG_CIRCLE` to `JIG_MOVING`.
  - Declare `void clip_clear(); void jig_cycle_scale();`.

- [ ] **Step 2: `jiggler.cpp`.** Mirror the Task 3 code. Differences:
  - `STEP_MS` 15;
  - `esp_random()` instead of `get_rand_32()`;
  - `out_mouse` / `out_key` instead of `usb_mouse`;
  - keep `jiggler_on_output_change()` (it resets to `JIG_MOVING`);
  - add `jiggler_demo_begin()`.

- [ ] **Step 3: `display.cpp`: partial push**

```cpp
void display_push_rect(int x, int y, int w, int h) {
    lcd.startWrite();
    lcd.setAddrWindow(x, y, w, h);
    for (int r = 0; r < h; r++) lcd.writePixels((const lgfx::rgb565_t *)(fb + (y + r) * LCD_W + x), w);
    lcd.endWrite();
}
```

Add the declaration to `display.h`.

- [ ] **Step 4: `ui.h` and `ui.cpp` layout** (round geometry from Global Constraints)

`ui.h`:

```cpp
#define TITLE_Y     42
#define BTN_Y       140
#define BTN_H       40
#define BTN_W       79
#define BTN_COPY_X  38
#define BTN_PASTE_X 123
#define CLIP_STATUS_Y 196
#define TRASH_CX    178
#define TRASH_CY    42
#define TRASH_HIT   18
#define PILL_Y      111
#define PILL_H      18
#define PILL_W      44
#define SCALE_PILL_X 10
#define ONOFF_PILL_X 186
#define PILL_PAD    6
#define JIG_ZONE_PAD 10
```

Remove `JIG_CX`, `JIG_CY` and `JIG_R`.

`ui.cpp` changes:
- All three titles use `TITLE_Y`. The Bluetooth sub-page title stays at 49, because its back chevron is laid out around it.
- `draw_clip`: box `CX0 32, CY0 52, CW 176, CH 80, ROWS 5`; trash as on the RP2040 but coloured with `accent()`-neutral `C_TEXT` / `C_FAINT`; status or progress at `CLIP_STATUS_Y` below the buttons.
- `draw_jig`: pills at `PILL_Y` beside the letter. Scale pill: `C_SURF2` / `accent()`. ON pill: `accent()` ring with `C_PC_TINT` or `C_BT_TINT`. Then the lane and the dot as on the RP2040; the status line and stats stay at 190/206.
- `ui_task`:
  - full frame on `redraw_seq` change, or once a second on the jiggler page for the countdown;
  - otherwise, while `app.jig_on || app.anim_demo` on the jiggler page, a partial frame every 50 ms: redraw clipped to the dot's old+new box and the status rect `{0, 180, 240, 34}`, then push with `display_push_rect`;
  - keep the existing 100 ms full frames while pasting.

- [ ] **Step 5: `main.cpp`**
  - Touch handling as in Task 4 Step 6, using `in_rect`/`near` and `prefs.putBool("jig", …)` after a toggle.
  - `jig_cycle_scale()` saves `prefs.putUChar("jscale", app.jig_scale_idx)`.
  - `setup()` reads `jscale` (clamped to 0..2) before `jiggler_set`.
  - `clip_clear()` as on the RP2040, with `xSemaphoreTake/Give(clip_mtx)`.

- [ ] **Step 6: `link.cpp`**
  - The same commands and `state_poll()` as Task 5, with `link_send_line`, `ANIM 1|0` and the header comment.
  - `DBG` adds `jscale=%.1f letter=%c`.
  - `loop()` calls `link_state_poll()`.

- [ ] **Step 7: board tests for the ESP32** (in `test_board_pc_mode.py`, skipped without COM7)
  - The same five tests as Task 5, using the `board` fixture there.
  - `test_jig_on_off_command` runs in `MODE PC` with `clip_helper` absent, so no input is performed.
  - Update the existing jiggler tap in `jiggler_off()` from `TAP 120 115` to `TAP 120 120`, which is inside the new zone.

- [ ] **Step 8: Build and test**

Run: `cd esp32c3 && python -m platformio run` (expected: SUCCESS), then `python -m pytest tools/tests -q` (expected: pass, with the ESP32 tests skipped).

- [ ] **Step 9: Commit**

```bash
git add esp32c3/src tools/tests/test_board_pc_mode.py
git commit -m "ESP32-C3: letter-lane jiggler with scale pill, clipboard trash + relayout, WATCH/STATE protocol, partial redraw"
```

---

### Task 7: App Core (STATE parsing, session commands, old-firmware detection)

**Files:**
- Modify: `windows-app/src/TouchDeck.Core/Protocol/BoardLine.cs`, `Session/DeviceSession.cs`
- Test: `windows-app/tests/TouchDeck.Tests/ProtocolTests.cs`, `SessionTests.cs`, and a new `JigPathsTests.cs`

**Interfaces:**
- Consumes: `JigPaths` (generated in Task 2).
- Produces:
  - `record StateReport(bool JigOn, char Letter, int Scale, int Phase, int X, int Y, int ClipLength, bool Pasting) : BoardMessage`;
  - `DeviceSession`:
    - `event Action<StateReport>? StateReceived`;
    - `StateReport? LastState`;
    - `bool? MirrorSupported` (null = unknown yet, false = no `STATE` within 1.5 s of `WATCH 1`);
    - `void SetJiggler(bool on)`, `void SetScale(int index)`, `void ClearClip()`.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests.cs`:

```csharp
    [Fact]
    public void Parses_state()
    {
        var m = BoardLine.Parse("STATE jig=1 letter=W scale=2 phase=0 x=412 y=733 clip=58 paste=0");
        Assert.Equal(new StateReport(true, 'W', 2, 0, 412, 733, 58, false), m);
    }

    [Fact]
    public void State_with_unknown_fields_still_parses()
    {
        var m = BoardLine.Parse("STATE jig=0 letter=O scale=0 phase=0 x=0 y=500 clip=0 paste=0 extra=7");
        Assert.IsType<StateReport>(m);
    }

    [Theory]
    [InlineData("STATE jig=1")]
    [InlineData("STATE jig=1 letter=W scale=x phase=0 x=1 y=2 clip=0 paste=0")]
    public void Malformed_state_is_unknown(string line) => Assert.IsType<UnknownLine>(BoardLine.Parse(line));
```

`SessionTests.cs`:

```csharp
    [Fact]
    public void Handshake_turns_on_the_mirror()
    {
        t.Incoming.Enqueue("PONG");
        Assert.True(Make().Handshake());
        Assert.Contains("WATCH 1", t.Written);
    }

    [Fact]
    public void State_lines_raise_StateReceived_and_mark_mirror_supported()
    {
        var s = Make();
        StateReport? got = null;
        s.StateReceived += r => got = r;
        t.Incoming.Enqueue("STATE jig=1 letter=M scale=1 phase=0 x=10 y=20 clip=3 paste=0");
        s.Step();
        Assert.Equal('M', got!.Letter);
        Assert.True(s.MirrorSupported);
        Assert.Equal(got, s.LastState);
    }

    [Fact]
    public void Old_firmware_is_detected_by_missing_state()
    {
        t.Incoming.Enqueue("PONG");
        var s = Make();
        s.Handshake();
        s.Step();
        clock.Now = clock.Now.AddSeconds(2);
        s.Step();
        Assert.False(s.MirrorSupported);
    }

    [Fact]
    public void Jiggler_and_clip_commands_are_sent()
    {
        var s = Make();
        s.SetJiggler(true); s.SetJiggler(false); s.SetScale(2); s.ClearClip();
        s.Step();
        Assert.Equal(["JIG ON", "JIG OFF", "JIG SCALE 2", "CLIP CLEAR"],
            t.Written.Where(w => w.StartsWith("JIG") || w.StartsWith("CLIP CLEAR")).ToList());
    }
```

`JigPathsTests.cs`:

```csharp
using TouchDeck.Core.Jiggler;
namespace TouchDeck.Tests;
public class JigPathsTests
{
    [Fact]
    public void Has_the_fifteen_letters_with_loop_flags()
    {
        Assert.Equal("OWMNZXCVHJLBGD", string.Concat(JigPaths.All.Select(l => l.Name)));
        Assert.Equal("OBD", string.Concat(JigPaths.All.Where(l => l.Closed).Select(l => l.Name)));
        Assert.All(JigPaths.All, l => Assert.All(l.Points, p => Assert.InRange(p.X, 0, 1000)));
        Assert.Null(JigPaths.Find('Q'));
    }
}
```

- [ ] **Step 2: Run and see them fail**

Run: `cd windows-app && dotnet test TouchDeck.sln`
Expected: compile errors (`StateReport`, `SetJiggler`, … missing).

- [ ] **Step 3: Implement**

`BoardLine.cs`:

```csharp
/// <summary>Live board state after WATCH 1 (firmware 1.6.0+). X/Y: jiggler dot in 0..1000 letter-box units.</summary>
public sealed record StateReport(bool JigOn, char Letter, int Scale, int Phase, int X, int Y, int ClipLength, bool Pasting)
    : BoardMessage;
```

In `Parse`: `case "STATE" when TryState(parts, out var st): return st;`, with:

```csharp
    private static bool TryState(string[] parts, out StateReport state)
    {
        state = null!;
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) f[p[..eq]] = p[(eq + 1)..];
        }
        int Num(string k) => f.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : int.MinValue;
        int jig = Num("jig"), scale = Num("scale"), phase = Num("phase"), x = Num("x"), y = Num("y"), clip = Num("clip"), paste = Num("paste");
        if (!f.TryGetValue("letter", out var letter) || letter.Length != 1 ||
            new[] { jig, scale, phase, x, y, clip, paste }.Contains(int.MinValue)) return false;
        state = new StateReport(jig == 1, letter[0], scale, phase, x, y, clip, paste == 1);
        return true;
    }
```

`DeviceSession.cs`:
- the handshake ends with `transport.WriteLine("WATCH 1"); watchSentAt = clock.Now;`;
- `Handle` gains `case StateReport st: LastState = st; MirrorSupported = true; StateReceived?.Invoke(st); break;`;
- in `Step`, after timers: `if (MirrorSupported is null && watchSentAt is { } w && clock.Now - w >= TimeSpan.FromSeconds(1.5)) MirrorSupported = false;`;
- the commands queue lines: `JIG ON`, `JIG OFF`, `JIG SCALE {index}` (only for 0..2) and `CLIP CLEAR`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test TouchDeck.sln`
Expected: all pass. Hardware tests pass on COM6 with the 1.6.0 board.

- [ ] **Step 5: Commit**

```bash
git add windows-app/src/TouchDeck.Core windows-app/tests
git commit -m "App core: STATE mirror, jiggler/clip commands, old-firmware detection"
```

---

### Task 8: App UI (jiggler card, board clip line, fallback)

**Files:**
- Create: `windows-app/src/TouchDeck.App/JiggleLane.cs` (a `FrameworkElement` that draws a lane and the dot)
- Modify: `windows-app/src/TouchDeck.App/AppController.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `windows-app/Directory.Build.props` (1.1.0)

**Interfaces:**
- Consumes: `DeviceSession.StateReceived`, `MirrorSupported`, `SetJiggler`, `SetScale`, `ClearClip`, `JigPaths.Find`.
- Produces:
  - `AppController` properties: `JigOn`, `JigLetter`, `JigScaleText`, `JigStatus`, `JigX`, `JigY`, `BoardClipText`, `CanClearBoardClip`, `MirrorAvailable`, `MirrorFallbackText`;
  - `AppController` methods: `ToggleJiggler()`, `CycleScale()`, `ClearBoardClip()`.

- [ ] **Step 1: `JiggleLane.cs`**

```csharp
using System.Windows;
using System.Windows.Media;
using TouchDeck.Core.Jiggler;

namespace TouchDeck.App;

/// <summary>A jiggler letter drawn like the device: wide accent stroke, inner lane, dot.</summary>
public sealed class JiggleLane : FrameworkElement
{
    public static readonly DependencyProperty LetterProperty = Reg(nameof(Letter), 'O');
    public static readonly DependencyProperty DotXProperty = Reg(nameof(DotX), 0.0);
    public static readonly DependencyProperty DotYProperty = Reg(nameof(DotY), 0.0);
    public static readonly DependencyProperty ActiveProperty = Reg(nameof(Active), false);

    public char Letter { get => (char)GetValue(LetterProperty); set => SetValue(LetterProperty, value); }
    public double DotX { get => (double)GetValue(DotXProperty); set => SetValue(DotXProperty, value); }
    public double DotY { get => (double)GetValue(DotYProperty); set => SetValue(DotYProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    private static DependencyProperty Reg<T>(string name, T def) => DependencyProperty.Register(name, typeof(T),
        typeof(JiggleLane), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        if (JigPaths.Find(Letter) is not { } l) return;
        // Same proportions as the RP2040 screen: 98 px box, 18 px lane, 2 px walls, 5.5 px dot.
        double size = Math.Min(ActualWidth, ActualHeight) * 98 / 120, k = size / 98;
        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        Point P(double x, double y) => new(ox + x * size / 1000, oy + y * size / 1000);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(P(l.Points[0].X, l.Points[0].Y), false, l.Closed);
            g.PolyLineTo(l.Points.Skip(1).Select(p => P(p.X, p.Y)).ToList(), true, true);
        }
        geo.Freeze();
        var accent = (Brush)FindResource(Active ? "Accent" : "Surf2");
        dc.DrawGeometry(null, new Pen(accent, (18 + 4) * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(null, new Pen((Brush)FindResource("Inner"), 18 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
        if (Active) dc.DrawEllipse((Brush)FindResource("Accent"), null, P(DotX, DotY), 5.5 * k, 5.5 * k);
    }
}
```

- [ ] **Step 2: `AppController`**
  - In `OnSessionStarted`, subscribe `s.StateReceived += st => Post(() => ApplyBoardState(st));`.
  - `ApplyBoardState` sets `JigOn`, `JigLetter`, `JigScaleText` (`["1.0X","1.5X","2.0X"][Scale]`), `JigX`/`JigY`, `JigStatus` (phase 0 "Moving", 1 "Pausing", 2–3 "Right-click menu", 4 "Esc", 5 "Resuming"; "Paused: pasting" when `Pasting`), `BoardClipText` (`"Board clip: N chars"` or `"Board clip: empty"`), and `CanClearBoardClip = ClipLength > 0 && !Pasting`.
  - A 1 s dispatcher timer copies `session.MirrorSupported` into `MirrorAvailable`. When it's false, `MirrorFallbackText = "Update the board's firmware to see and control the jiggler here."`.
  - Methods call the session: `ToggleJiggler() => manager.Session?.SetJiggler(!JigOn)`, `CycleScale() => …SetScale((scaleIndex + 1) % 3)`, `ClearBoardClip() => …ClearClip()`.

- [ ] **Step 3: `MainWindow.xaml`**
  - A new "Jiggler" card in the left column between Device and Remote, containing:
    - a top row with the scale pill button (left, `Click="OnScale"`, content `{Binding JigScaleText}`) and the ON/OFF pill button (right, `Click="OnJigToggle"`, content ON/OFF by `JigOn`);
    - `<local:JiggleLane Height="150" Letter="{Binding JigLetter}" DotX="{Binding JigX}" DotY="{Binding JigY}" Active="{Binding JigOn}" MouseLeftButtonUp="OnJigToggle"/>`;
    - the status text `{Binding JigStatus}`;
    - a fallback `TextBlock` bound to `MirrorFallbackText`, visible when `MirrorAvailable` is false.
  - On the Send card, under the send row: `TextBlock {Binding BoardClipText}` plus a small "Clear" button with a trash glyph (`Path` data for a bin), bound to `IsEnabled="{Binding CanClearBoardClip}"`, `Click="OnClearBoardClip"`.
  - Window `Height` becomes 860.

- [ ] **Step 4: Version and smoke**

Set `windows-app/Directory.Build.props` `<Version>1.1.0</Version>`.

Run:

```
cd windows-app
dotnet build TouchDeck.sln
src\TouchDeck.App\bin\Debug\net8.0-windows\TouchDeck.exe --smoke <scratch>\smoke
```

Expected: `smoke.txt` has `status=Connected` and `firmware=1.6.0`. `smoke.png` shows the jiggler card with the board's current letter; check it by reading the PNG. Add `jig=` and `letter=` lines to `smoke.txt` in `RunSmokeAsync`.

- [ ] **Step 5: Commit**

```bash
git add windows-app
git commit -m "App: live jiggler card (letter lane, dot, ON/OFF, scale), board clip line with clear, fallback for old firmware"
```

---

### Task 9: Versions, full verification, docs, review, PR (the "done" gate)

**Files:**
- Modify: `src/version.h`, `esp32c3/src/version.h` (1.6.0), `README.md`, `CLAUDE.md`, `windows-app/CLAUDE.md`, `windows-app/README.md`, `windows-app/docs/img/main-window.png`

- [ ] **Step 1: Bump firmware**

Set `#define FW_VERSION "1.6.0"` in both `version.h` files. Rebuild both firmwares, `python tools/flash.py`, and confirm `VER` answers `1.6.0`.

- [ ] **Step 2: The green gate.** Every item must pass; fix and repeat until it does.
  - `python -m pytest tools/tests -q`: all pass. The ESP32 board tests skip if it's absent.
  - `cd esp32c3 && python -m platformio run`: SUCCESS.
  - `ninja -C build`: clean.
  - `cd windows-app && .\build.ps1 -Smoke`: 0 test failures; MSI `TouchDeck-1.1.0.msi` built; smoke `firmware=1.6.0`.
  - `.\tools\install-smoke.ps1 -KeepInstalled`: all checks pass, including "board firmware version read".
  - `python tools/perf_rp2040.py`: jiggler `drawmax` under 25 ms during animation frames. Full frames on letter switch are allowed to exceed that; record the numbers.
  - A 10-minute real run with the jiggler on. The mouse stays within about 170 px of where it started at 1x, and letters change after each menu.

- [ ] **Step 3: Docs**
  - `README.md`: jiggler letters and pills, trash can, tick-only mute, the app's jiggler card.
  - `CLAUDE.md`:
    - the new files (`jigpaths.py` generator, never hand-edit `jig_paths.*`; shared `jig_motion.*`, `jig_lane.*` must stay identical in both trees);
    - the protocol lines;
    - the dirty-box animation;
    - tick-only mute;
    - test commands (host engine tests need MSVC).
  - `windows-app/CLAUDE.md` and `README.md`: `StateReport`/`WATCH`, the jiggler card, a new screenshot (copy the smoke PNG to `docs/img/main-window.png`).

- [ ] **Step 4: Fresh review**

Dispatch one code-review subagent over `git diff main...HEAD`, focused on:
- cross-core races (`app.jig_*` read by core1 while core0 writes);
- stuck input on `JIG OFF` mid-menu;
- protocol parsing robustness;
- the engine's bounds;
- the ESP32 partial push.

Fix confirmed findings with tests, then re-run the Step 2 gate.

- [ ] **Step 5: Commit, push, PR**

```bash
git add -A
git commit -m "Firmware 1.6.0, app 1.1.0: docs and screenshots"
git push -u origin feature/jiggler-letters
```

Give the user the compare link: https://github.com/SynexyConsulting/TouchDeck/compare/main...feature/jiggler-letters, with a paste-ready title and body. `gh` is not installed.
