// Tiny signed-distance renderer: every shape computes, per pixel, the distance
// to its edge and turns the fractional part into coverage for anti-aliasing.
// Circles fill their fully-covered interior as plain spans and only run the
// distance math on edge pixels.
#include "gfx.h"
#include "board.h"
#include <math.h>
#include <stdbool.h>
#include <string.h>

uint16_t fb[LCD_W * LCD_H];

// Clip rectangle (half-open): every primitive only touches pixels inside it and
// narrows its loops to it, so redrawing a small region is cheap.
static int clip_x0, clip_y0, clip_x1 = LCD_W, clip_y1 = LCD_H;

void gfx_set_clip(int x, int y, int w, int h) {
    clip_x0 = x < 0 ? 0 : x;
    clip_y0 = y < 0 ? 0 : y;
    clip_x1 = x + w > LCD_W ? LCD_W : x + w;
    clip_y1 = y + h > LCD_H ? LCD_H : y + h;
}

void gfx_clip_reset(void) { gfx_set_clip(0, 0, LCD_W, LCD_H); }

void gfx_fill(uint16_t color) {
    for (int y = clip_y0; y < clip_y1; y++)
        for (int x = clip_x0; x < clip_x1; x++) fb[y * LCD_W + x] = color;
}

void gfx_rect(int x, int y, int w, int h, uint16_t color) {
    int x1 = x + w, y1 = y + h;
    if (x < clip_x0) x = clip_x0;
    if (y < clip_y0) y = clip_y0;
    if (x1 > clip_x1) x1 = clip_x1;
    if (y1 > clip_y1) y1 = clip_y1;
    for (int yy = y; yy < y1; yy++)
        for (int xx = x; xx < x1; xx++) fb[yy * LCD_W + xx] = color;
}

static inline uint16_t blend(uint16_t bg, uint16_t fg, int a /*0..32*/) {
    uint32_t b = (bg | ((uint32_t)bg << 16)) & 0x07E0F81F;
    uint32_t f = (fg | ((uint32_t)fg << 16)) & 0x07E0F81F;
    b += ((f - b) * a) >> 5;
    b &= 0x07E0F81F;
    return (uint16_t)(b | (b >> 16));
}

static inline void plot(int x, int y, uint16_t c, float coverage) {
    if (coverage <= 0.f || x < clip_x0 || x >= clip_x1 || y < clip_y0 || y >= clip_y1) return;
    uint16_t *p = &fb[y * LCD_W + x];
    *p = coverage >= 1.f ? c : blend(*p, c, (int)(coverage * 32.f));
}

static void clip_box(float x0, float y0, float x1, float y1, int *bx0, int *by0, int *bx1, int *by1) {
    *bx0 = (int)floorf(x0); if (*bx0 < clip_x0) *bx0 = clip_x0;
    *by0 = (int)floorf(y0); if (*by0 < clip_y0) *by0 = clip_y0;
    *bx1 = (int)ceilf(x1);  if (*bx1 > clip_x1 - 1) *bx1 = clip_x1 - 1;
    *by1 = (int)ceilf(y1);  if (*by1 > clip_y1 - 1) *by1 = clip_y1 - 1;
}

void gfx_disc(float cx, float cy, float r, uint16_t color) {
    float ro = r + 0.5f, ri = r - 0.5f;   // coverage > 0 outside ri, == 1 inside ri
    int y0 = (int)floorf(cy - ro), y1 = (int)ceilf(cy + ro);
    if (y0 < clip_y0) y0 = clip_y0;
    if (y1 > clip_y1 - 1) y1 = clip_y1 - 1;
    for (int y = y0; y <= y1; y++) {
        float dy = y + 0.5f - cy, dy2 = dy * dy;
        if (dy2 >= ro * ro) continue;
        float xo = sqrtf(ro * ro - dy2);
        int ex0 = (int)floorf(cx - xo), ex1 = (int)ceilf(cx + xo);
        if (ex0 < clip_x0) ex0 = clip_x0;
        if (ex1 > clip_x1 - 1) ex1 = clip_x1 - 1;
        int fx0 = ex1 + 1, fx1 = ex1;       // solid span, empty by default
        if (ri > 0.f && dy2 < ri * ri) {
            float xi = sqrtf(ri * ri - dy2);
            fx0 = (int)ceilf(cx - xi - 0.5f);
            fx1 = (int)floorf(cx + xi - 0.5f);
        }
        for (int x = ex0; x <= ex1; x++) {
            if (x >= fx0 && x <= fx1) {
                int a = fx0 < clip_x0 ? clip_x0 : fx0, b = fx1 > clip_x1 - 1 ? clip_x1 - 1 : fx1;
                for (int xx = a; xx <= b; xx++) fb[y * LCD_W + xx] = color;
                x = fx1;
                continue;
            }
            float dx = x + 0.5f - cx;
            plot(x, y, color, r + 0.5f - sqrtf(dx * dx + dy2));
        }
    }
}

void gfx_ring(float cx, float cy, float r, float width, uint16_t color) {
    float ro = r + width * 0.5f, ri = r - width * 0.5f;
    float bo = ro + 0.5f, bi = ri - 0.5f;   // outside bo / inside bi: untouched
    int y0 = (int)floorf(cy - bo), y1 = (int)ceilf(cy + bo);
    if (y0 < clip_y0) y0 = clip_y0;
    if (y1 > clip_y1 - 1) y1 = clip_y1 - 1;
    for (int y = y0; y <= y1; y++) {
        float dy = y + 0.5f - cy, dy2 = dy * dy;
        if (dy2 >= bo * bo) continue;
        float xo = sqrtf(bo * bo - dy2);
        float xi = (bi > 0.f && dy2 < bi * bi) ? sqrtf(bi * bi - dy2) : -1.f;
        int xs = (int)floorf(cx - xo), xe = (int)ceilf(cx + xo);
        if (xs < clip_x0) xs = clip_x0;
        if (xe > clip_x1 - 1) xe = clip_x1 - 1;
        for (int x = xs; x <= xe; x++) {
            float dx = x + 0.5f - cx;
            if (fabsf(dx) < xi - 1.f) {      // jump over the hole
                x = (int)floorf(cx + xi - 1.f);
                continue;
            }
            float d = sqrtf(dx * dx + dy2);
            float a = fminf(ro + 0.5f - d, d - ri + 0.5f);
            plot(x, y, color, a > 1.f ? 1.f : a);
        }
    }
}

void gfx_line(float ax, float ay, float bx, float by, float thick, uint16_t color) {
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

void gfx_rrect(int x, int y, int w, int h, float rad, uint16_t color) {
    // Same coverage as the per-pixel rounded-box distance, but only corner rows
    // need maths: between the corners every pixel of a row is fully covered.
    float hx = w * 0.5f, hy = h * 0.5f, cx = x + hx, cy = y + hy;
    int in0 = (int)ceilf(cx - (hx - rad) - 0.5f);    // columns whose centre is between the corners
    int in1 = (int)floorf(cx + (hx - rad) - 0.5f);
    int ys = y > clip_y0 ? y : clip_y0, ye = y + h < clip_y1 ? y + h : clip_y1;
    int xs = x > clip_x0 ? x : clip_x0, xe = x + w < clip_x1 ? x + w : clip_x1;
    for (int py = ys; py < ye; py++) {
        float qy = fabsf(py + 0.5f - cy) - (hy - rad);
        if (qy <= 0.f) {                             // straight band: solid
            gfx_rect(x, py, w, 1, color);
            continue;
        }
        float band = 0.5f - (qy - rad);              // coverage between the corners on this row
        for (int px = xs; px < xe; px++) {
            if (px >= in0 && px <= in1) {
                if (band >= 1.f) { gfx_rect(px, py, in1 - px + 1, 1, color); px = in1; }
                else plot(px, py, color, band);
                continue;
            }
            float qx = fabsf(px + 0.5f - cx) - (hx - rad);
            plot(px, py, color, 0.5f - (sqrtf(qx * qx + qy * qy) - rad));
        }
    }
}

void gfx_rrect_ring(int x, int y, int w, int h, float rad, float width, uint16_t color) {
    // Only pixels near the outline are touched. Between the corners the
    // coverage along a row is constant, so only corner pixels need a sqrt.
    float hx = w * 0.5f, hy = h * 0.5f, cx = x + hx, cy = y + hy;
    int in0 = (int)ceilf(cx - (hx - rad) - 0.5f), in1 = (int)floorf(cx + (hx - rad) - 0.5f);
    int edge = (int)width + 2;
    int ys = y > clip_y0 ? y : clip_y0, ye = y + h < clip_y1 ? y + h : clip_y1;
    int xs = x > clip_x0 ? x : clip_x0, xe = x + w < clip_x1 ? x + w : clip_x1;
    for (int py = ys; py < ye; py++) {
        float qy = fabsf(py + 0.5f - cy) - (hy - rad);
        for (int px = xs; px < xe; px++) {
            bool middle = px >= in0 && px <= in1;
            if (qy <= 0.f && px >= x + edge && px < x + w - edge) {
                px = x + w - edge - 1;               // side band: skip the untouched middle
                continue;
            }
            float d;
            if (middle) {
                d = qy - rad;                         // constant along this row
                float a = fminf(0.5f - d, d + width + 0.5f);
                if (a <= 0.f) { px = in1; continue; }  // row is inside or outside the outline
                if (a >= 1.f) { gfx_rect(px, py, in1 - px + 1, 1, color); px = in1; continue; }
                plot(px, py, color, a);
                continue;
            }
            float qx = fabsf(px + 0.5f - cx) - (hx - rad);
            d = (qx > 0.f && qy > 0.f) ? sqrtf(qx * qx + qy * qy) - rad : fmaxf(qx, qy) - rad;
            float a = fminf(0.5f - d, d + width + 0.5f);   // inside the outer edge, outside the inner
            plot(px, py, color, a > 1.f ? 1.f : a);
        }
    }
}


static const aa_glyph_t *aa_glyph(const aa_font_t *f, char c) {
    if (c < ' ' || c > '~') c = '?';
    return &f->glyphs[c - ' '];
}

int gfx_text_aa_width(const char *s, const aa_font_t *f, int spacing) {
    int w = 0, n = 0;
    for (; *s; s++, n++) w += aa_glyph(f, *s)->adv;
    return n ? w + spacing * (n - 1) : 0;
}

int gfx_text_aa(int x, int y, const char *s, const aa_font_t *f, uint16_t color, int spacing) {
    for (; *s; s++) {
        const aa_glyph_t *g = aa_glyph(f, *s);
        int gx = x + g->x, gy = y + g->y;
        if (gx >= clip_x1 || gy >= clip_y1 || gx + g->w <= clip_x0 || gy + g->h <= clip_y0) {
            x += g->adv + spacing;
            continue;
        }
        const uint8_t *bits = f->bitmap + g->offset;
        int bpr = (g->w + 1) / 2;
        for (int r = 0; r < g->h; r++)
            for (int col = 0; col < g->w; col++) {
                uint8_t v = bits[r * bpr + col / 2];
                int a = (col & 1) ? (v & 0x0F) : (v >> 4);
                if (a) plot(x + g->x + col, y + g->y + r, color, a / 15.f);
            }
        x += g->adv + spacing;
    }
    return x;
}

void gfx_text_aa_centered(int cx, int y, const char *s, const aa_font_t *f, uint16_t color, int spacing) {
    gfx_text_aa(cx - gfx_text_aa_width(s, f, spacing) / 2, y, s, f, color, spacing);
}

int gfx_text_aa_ytop(const aa_font_t *f, int cy) { return cy - f->cap_top - f->cap_h / 2; }
