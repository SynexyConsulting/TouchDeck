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
