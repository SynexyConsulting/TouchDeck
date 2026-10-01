// The RP2040 Touch Deck's pages, drawn into fb from a ui_state_t. Plain C with
// no SDK calls: the firmware (ui.c) and the PC app's device mirror (hostui/)
// both run it, so the app shows exactly what the device does.
#pragma once
#ifdef TD_ROUND
#include "round/ui_pages.h"   // the RP2350 round board draws the ESP32-C3's round pages
#else
#include "ui_state.h"
#ifdef __cplusplus
extern "C" {
#endif

enum { SCR_WATCH, SCR_CLIP, SCR_JIG, SCR_COUNT };
// ui_state_t.sub: a panel open over a page. UI_SUB_JIGSET = the Jiggler settings,
// opened by the cog on the Jiggler page (same ids on every board).
enum { UI_SUB_NONE, UI_SUB_BT, UI_SUB_JIGSET };

typedef struct { int x, y, w, h; } rect_t;

// The whole page for s->screen (honours the gfx clip, so a clipped call
// redraws just that region).
void ui_draw_page(const ui_state_t *s);

// Regions for partial frames.
rect_t ui_hands_rect(int t);                 // watch hands at time t (seconds of day)
rect_t ui_stopwatch_rect(void);              // the stopwatch box
rect_t ui_dot_rect(float bx, float by);      // the jiggler dot at box position (bx, by)
rect_t ui_rect_union(rect_t a, rect_t b);

#ifdef __cplusplus
}
#endif
#endif   // TD_ROUND
