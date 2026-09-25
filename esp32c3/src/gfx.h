#pragma once
#include <stdint.h>
#include "fonts/fonts.h"

#ifdef __cplusplus
extern "C" {
#endif

#define RGB(r, g, b) ((uint16_t)((((r) & 0xF8) << 8) | (((g) & 0xFC) << 3) | ((b) >> 3)))

extern uint16_t *fb;

void gfx_fill(uint16_t color);
void gfx_rect(int x, int y, int w, int h, uint16_t color);
// Anti-aliased primitives, blended against what is already in fb.
void gfx_disc(float cx, float cy, float r, uint16_t color);
void gfx_ring(float cx, float cy, float r, float width, uint16_t color);
// A line with round caps ("capsule") of the given thickness.
void gfx_line(float x0, float y0, float x1, float y1, float thick, uint16_t color);
void gfx_rrect(int x, int y, int w, int h, float radius, uint16_t color);

// 1-bit fonts (Font12 7x12, Font16 11x16, Font24 17x24), printable ASCII only.
void gfx_char(int x, int y, char c, const sFONT *font, uint16_t color);
void gfx_text(int x, int y, const char *s, const sFONT *font, uint16_t color);
void gfx_text_centered(int cx, int y, const char *s, const sFONT *font, uint16_t color);

#ifdef __cplusplus
}
#endif
