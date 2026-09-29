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
