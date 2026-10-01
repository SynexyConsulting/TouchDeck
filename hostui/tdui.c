// Touch Deck device renderer for the PC apps (the device mirror). Built once per
// board from that board's own firmware sources (ui_pages.c, gfx.c, fonts, icons,
// jiggler lanes), so the app draws exactly the pixels the device does.
// Build: hostui/build.bat (Windows, MSVC) or hostui/build.sh (macOS/Linux).
#include <string.h>
#include "board.h"
#include "gfx.h"
#include "jig_paths.h"
#include "ui_pages.h"
#include "ui_sync.h"
#include "tdui.h"

#ifdef TDUI_FB_PTR                   // ESP32-C3 tree: fb is a pointer the firmware allocates
static uint16_t frame[LCD_W * LCD_H];
#endif

TDUI_API int tdui_width(void) { return LCD_W; }
TDUI_API int tdui_height(void) { return LCD_H; }
TDUI_API int tdui_state_size(void) { return (int)sizeof(ui_state_t); }

TDUI_API int tdui_letter_index(char name) {
    for (int i = 0; i < JIG_PATH_COUNT; i++)
        if (JIG_PATHS[i].name == name) return i;
    return -1;
}

TDUI_API void tdui_render(const ui_state_t *s, uint16_t *out) {
#ifdef TDUI_FB_PTR
    fb = frame;
#endif
    gfx_clip_reset();
    ui_draw_page(s);
    memcpy(out, fb, sizeof(uint16_t) * LCD_W * LCD_H);
}

TDUI_API int tdui_state_line(const ui_state_t *s, char *out, int n) { return ui_sync_state_line(s, out, n); }
TDUI_API int tdui_clip_line(const char *clip, int len, char *out, int n) { return ui_sync_clip_line(clip, len, out, n); }

static void put_rect(rect_t r, int *xywh) { xywh[0] = r.x; xywh[1] = r.y; xywh[2] = r.w; xywh[3] = r.h; }
TDUI_API void tdui_hands_rect(int t, int *xywh) { put_rect(ui_hands_rect(t), xywh); }
TDUI_API void tdui_stopwatch_rect(int *xywh) { put_rect(ui_stopwatch_rect(), xywh); }
