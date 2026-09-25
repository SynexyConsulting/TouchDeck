// Tiny signed-distance renderer: every shape computes, per pixel, the distance
// to its edge and turns the fractional part into coverage for anti-aliasing.
// Circles fill their fully-covered interior as plain spans and only run the
// distance math on edge pixels.
#include "gfx.h"
#include "board.h"
#include <math.h>
#include <string.h>

uint16_t *fb;   // allocated at startup (DMA-capable RAM), see display.cpp

void gfx_fill(uint16_t color) {
    for (int i = 0; i < LCD_W * LCD_H; i++) fb[i] = color;
}

void gfx_rect(int x, int y, int w, int h, uint16_t color) {
    int x1 = x + w, y1 = y + h;
    if (x < 0) x = 0;
    if (y < 0) y = 0;
    if (x1 > LCD_W) x1 = LCD_W;
    if (y1 > LCD_H) y1 = LCD_H;
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
    if (coverage <= 0.f || x < 0 || x >= LCD_W || y < 0 || y >= LCD_H) return;
    uint16_t *p = &fb[y * LCD_W + x];
    *p = coverage >= 1.f ? c : blend(*p, c, (int)(coverage * 32.f));
}

static void clip_box(float x0, float y0, float x1, float y1, int *bx0, int *by0, int *bx1, int *by1) {
    *bx0 = (int)floorf(x0); if (*bx0 < 0) *bx0 = 0;
    *by0 = (int)floorf(y0); if (*by0 < 0) *by0 = 0;
    *bx1 = (int)ceilf(x1);  if (*bx1 > LCD_W - 1) *bx1 = LCD_W - 1;
    *by1 = (int)ceilf(y1);  if (*by1 > LCD_H - 1) *by1 = LCD_H - 1;
}

void gfx_disc(float cx, float cy, float r, uint16_t color) {
    float ro = r + 0.5f, ri = r - 0.5f;   // coverage > 0 outside ri, == 1 inside ri
    int y0 = (int)floorf(cy - ro), y1 = (int)ceilf(cy + ro);
    if (y0 < 0) y0 = 0;
    if (y1 > LCD_H - 1) y1 = LCD_H - 1;
    for (int y = y0; y <= y1; y++) {
        float dy = y + 0.5f - cy, dy2 = dy * dy;
        if (dy2 >= ro * ro) continue;
        float xo = sqrtf(ro * ro - dy2);
        int ex0 = (int)floorf(cx - xo), ex1 = (int)ceilf(cx + xo);
        int fx0 = ex1 + 1, fx1 = ex1;       // solid span, empty by default
        if (ri > 0.f && dy2 < ri * ri) {
            float xi = sqrtf(ri * ri - dy2);
            fx0 = (int)ceilf(cx - xi - 0.5f);
            fx1 = (int)floorf(cx + xi - 0.5f);
        }
        for (int x = ex0; x <= ex1; x++) {
            if (x >= fx0 && x <= fx1) {
                int a = fx0 < 0 ? 0 : fx0, b = fx1 > LCD_W - 1 ? LCD_W - 1 : fx1;
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
    if (y0 < 0) y0 = 0;
    if (y1 > LCD_H - 1) y1 = LCD_H - 1;
    for (int y = y0; y <= y1; y++) {
        float dy = y + 0.5f - cy, dy2 = dy * dy;
        if (dy2 >= bo * bo) continue;
        float xo = sqrtf(bo * bo - dy2);
        float xi = (bi > 0.f && dy2 < bi * bi) ? sqrtf(bi * bi - dy2) : -1.f;
        for (int x = (int)floorf(cx - xo); x <= (int)ceilf(cx + xo); x++) {
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
    float hx = w * 0.5f, hy = h * 0.5f, cx = x + hx, cy = y + hy;
    for (int py = y; py < y + h; py++)
        for (int px = x; px < x + w; px++) {
            float qx = fabsf(px + 0.5f - cx) - (hx - rad);
            float qy = fabsf(py + 0.5f - cy) - (hy - rad);
            float d;
            if (qx > 0.f && qy > 0.f) d = sqrtf(qx * qx + qy * qy) - rad;
            else d = fmaxf(qx, qy) - rad;
            plot(px, py, color, 0.5f - d);
        }
}

void gfx_char(int x, int y, char c, const sFONT *font, uint16_t color) {
    if (c < ' ' || c > '~') c = '?';
    int bpr = (font->Width + 7) / 8;
    const uint8_t *g = font->table + (c - ' ') * font->Height * bpr;
    for (int row = 0; row < font->Height; row++, g += bpr) {
        int yy = y + row;
        if (yy < 0 || yy >= LCD_H) continue;
        for (int col = 0; col < font->Width; col++) {
            int xx = x + col;
            if (xx < 0 || xx >= LCD_W) continue;
            if (g[col >> 3] & (0x80 >> (col & 7))) fb[yy * LCD_W + xx] = color;
        }
    }
}

void gfx_text(int x, int y, const char *s, const sFONT *font, uint16_t color) {
    for (; *s; s++, x += font->Width) gfx_char(x, y, *s, font, color);
}

void gfx_text_centered(int cx, int y, const char *s, const sFONT *font, uint16_t color) {
    gfx_text(cx - (int)strlen(s) * font->Width / 2, y, s, font, color);
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
