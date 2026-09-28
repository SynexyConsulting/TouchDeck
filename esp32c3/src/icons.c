#include <math.h>
#include "icons.h"
#include "gfx.h"

static float K, CX, CY, W;
static uint16_t COL;

static void begin(float cx, float cy, float size, uint16_t col) {
    K = size / 24.f; CX = cx; CY = cy; COL = col;
    W = K * 2.4f < 1.4f ? 1.4f : K * 2.4f;
}
static void seg(float x0, float y0, float x1, float y1) {
    gfx_line(CX + (x0 - 12) * K, CY + (y0 - 12) * K, CX + (x1 - 12) * K, CY + (y1 - 12) * K, W, COL);
}

void icon_bt(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(7, 7, 17, 17); seg(17, 17, 12, 22); seg(12, 22, 12, 2); seg(12, 2, 17, 7); seg(17, 7, 7, 17);
}
void icon_monitor(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(2, 4, 22, 4); seg(22, 4, 22, 17); seg(22, 17, 2, 17); seg(2, 17, 2, 4);
    seg(8, 21, 16, 21); seg(12, 17, 12, 21);
}
void icon_copy(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(8, 8, 21, 8); seg(21, 8, 21, 21); seg(21, 21, 8, 21); seg(8, 21, 8, 8);
    seg(16, 8, 16, 3); seg(16, 3, 3, 3); seg(3, 3, 3, 16); seg(3, 16, 8, 16);
}
void icon_arrow_right(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(5, 12, 19, 12); seg(13, 6, 19, 12); seg(19, 12, 13, 18);
}
void icon_lock(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    gfx_rrect((int)(cx - 7 * K), (int)(cy - 1 * K), (int)(14 * K + 1), (int)(10 * K + 1), 2 * K, c);
    seg(8, 11, 8, 8); seg(8, 8, 10, 4.5f); seg(10, 4.5f, 14, 4.5f); seg(14, 4.5f, 16, 8); seg(16, 8, 16, 11);
}
void icon_chevron_left(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(15, 6, 9, 12); seg(9, 12, 15, 18);
}
void icon_chevron_right(float cx, float cy, float s, uint16_t c) {
    begin(cx, cy, s, c);
    seg(9, 6, 15, 12); seg(15, 12, 9, 18);
}
void icon_cog(float cx, float cy, float s, uint16_t c) {
    float k = s / 24.f;
    for (int i = 0; i < 8; i++) {
        float a = i * 0.7854f;
        gfx_line(cx + cosf(a) * 6.f * k, cy + sinf(a) * 6.f * k,
                 cx + cosf(a) * 10.5f * k, cy + sinf(a) * 10.5f * k, 3.4f * k, c);
    }
    gfx_disc(cx, cy, 7.5f * k, c);
    gfx_disc(cx, cy, 3.f * k, RGB(7, 9, 13));
}
