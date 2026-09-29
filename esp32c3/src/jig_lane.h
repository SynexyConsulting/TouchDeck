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
