// The ESP32-C3 Touch Deck's pages, drawn into fb from a ui_state_t. Plain C
// with no Arduino calls: the firmware (ui.cpp) and the PC app's device mirror
// (hostui/) both run it, so the app shows exactly what the device does.
#pragma once
#include "ui_state.h"
#ifdef __cplusplus
extern "C" {
#endif

enum { SCR_CLIP, SCR_JIG, SCR_SETTINGS, SCR_COUNT };

// ui_state_t.bt_state values: the same numbers as bt_state_t (ble_hid.h, C++ only;
// ui.cpp checks they match).
enum { UI_BT_UNPAIRED, UI_BT_PAIRING, UI_BT_WAITING, UI_BT_CONNECTED, UI_BT_OFF };

typedef struct { int x, y, w, h; } rect_t;

// The whole page for s->screen (or the Bluetooth sub-page when s->sub); honours
// the gfx clip, so a clipped call redraws just that region.
void ui_draw_page(const ui_state_t *s);
rect_t ui_dot_rect(float bx, float by);      // the jiggler dot at box position (bx, by)
rect_t ui_rect_union(rect_t a, rect_t b);    // clamped to the screen

#ifdef __cplusplus
}
#endif
