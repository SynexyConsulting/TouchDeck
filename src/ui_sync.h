// The device mirror's sync lines (after WATCH 1): a ui_state_t as protocol
// text, so the PC app can draw the same pages. Plain C, identical in src/ and
// esp32c3/src/, and compiled into hostui/ so tests can check the app's parser
// against it. Protocol: the top of usb_io.c / link.cpp.
#pragma once
#include "ui_state.h"
#ifdef __cplusplus
extern "C" {
#endif

// What changed between two samples (ui_sync_diff).
enum {
    UI_SYNC_FIELDS = 1,     // any STATE number except the dot
    UI_SYNC_DOT = 2,        // the dot's position (whole box units, as sent)
    UI_SYNC_MSG = 4,        // TEXT msg
    UI_SYNC_SRC = 8,        // TEXT src
    UI_SYNC_HOST = 16,      // TEXT host
    UI_SYNC_DOWN = 32,      // TEXT down
    UI_SYNC_ALL = 63,
};

#define UI_SYNC_CLIP_LINE (10 + 4 * UI_CLIP_VIEW)   // room for the longest CLIPTEXT line

int ui_sync_diff(const ui_state_t *prev, const ui_state_t *cur);
// After a STATE line went out: copy the numbers it carried into prev, the same
// set ui_sync_diff compares (strings go with TEXT, the clip with CLIPTEXT).
void ui_sync_commit_fields(ui_state_t *prev, const ui_state_t *cur);
// "STATE jig=.. letter=.. ..." into out (n bytes); returns the length.
int ui_sync_state_line(const ui_state_t *s, char *out, int n);
// "TEXT <key> <value>": control characters in value become '?'.
int ui_sync_text_line(const char *key, const char *value, char *out, int n);
// "CLIPTEXT <escaped>" for the first UI_CLIP_VIEW bytes of clip: \\ \n \r \t
// and \xHH for anything else outside 0x20..0x7E, and for a final space (so a
// reader that trims lines keeps it).
int ui_sync_clip_line(const char *clip, int len, char *out, int n);

#ifdef __cplusplus
}
#endif
