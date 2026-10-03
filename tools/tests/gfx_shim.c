// PC-side test shim: compiles the firmware's gfx.c as-is (included, so its static
// helpers are visible) and keeps the original per-bounding-box gfx_line as the
// reference that the optimised one must match pixel for pixel.
#include "../../src/gfx.c"
#ifdef _WIN32
#define X __declspec(dllexport)
#else
#define X __attribute__((visibility("default")))
#endif

static void ref_gfx_line(float ax, float ay, float bx, float by, float thick, uint16_t color) {
    float r = thick * 0.5f;
    int x0, y0, x1, y1;
    clip_box(fminf(ax, bx) - r - 1, fminf(ay, by) - r - 1,
             fmaxf(ax, bx) + r + 1, fmaxf(ay, by) + r + 1, &x0, &y0, &x1, &y1);
    float vx = bx - ax, vy = by - ay;
    float len2 = vx * vx + vy * vy;
    float inv = len2 > 0.f ? 1.f / len2 : 0.f;
    for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++) {
            float px = x + 0.5f - ax, py = y + 0.5f - ay;
            float t = (px * vx + py * vy) * inv;
            t = t < 0.f ? 0.f : (t > 1.f ? 1.f : t);
            float dx = px - vx * t, dy = py - vy * t;
            plot(x, y, color, r + 0.5f - sqrtf(dx * dx + dy * dy));
        }
}

X void t_fill(uint16_t c) { gfx_clip_reset(); gfx_fill(c); }
X void t_clip(int x, int y, int w, int h) { gfx_set_clip(x, y, w, h); }
X void t_line(float ax, float ay, float bx, float by, float thick, uint16_t c) { gfx_line(ax, ay, bx, by, thick, c); }
X void t_ref_line(float ax, float ay, float bx, float by, float thick, uint16_t c) { ref_gfx_line(ax, ay, bx, by, thick, c); }
X const uint16_t *t_fb(void) { return fb; }
X int t_w(void) { return LCD_W; }
X int t_h(void) { return LCD_H; }
