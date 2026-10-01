// Touch Deck device renderer: the C API the PC apps load (tdui_rp2040 /
// tdui_esp32c3 as .dll, .dylib or .so). The state struct is ui_state.h.
#pragma once
#include <stdint.h>
#include "ui_state.h"

#if defined(_WIN32)
#define TDUI_API __declspec(dllexport)
#else
#define TDUI_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif
TDUI_API int tdui_width(void);
TDUI_API int tdui_height(void);
TDUI_API int tdui_state_size(void);                         // sizeof(ui_state_t): layout check
TDUI_API int tdui_letter_index(char name);                  // STATE letter= -> ui_state_t.jig_letter, -1 if unknown
TDUI_API void tdui_render(const ui_state_t *s, uint16_t *out);   // out: width*height RGB565
TDUI_API int tdui_state_line(const ui_state_t *s, char *out, int n);   // the board's STATE line (tests)
TDUI_API int tdui_clip_line(const char *clip, int len, char *out, int n);   // the board's CLIPTEXT line (tests)
// Watch partial-redraw boxes (x, y, w, h): the hands at time t, and the stopwatch (tests).
TDUI_API void tdui_hands_rect(int t, int *xywh);
TDUI_API void tdui_stopwatch_rect(int *xywh);
#ifdef __cplusplus
}
#endif
