// PC-side test shim: exports the engine through plain functions for ctypes.
#include "jig_motion.h"
#ifdef _WIN32
#define X __declspec(dllexport)
#else
#define X __attribute__((visibility("default")))
#endif
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
