// The device mirror's sync lines. See ui_sync.h.
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include "jig_paths.h"
#include "ui_sync.h"

int ui_sync_diff(const ui_state_t *a, const ui_state_t *b) {
    int d = 0;
    // Every number before jig_x, then the ones after the dot up to the strings.
    if (memcmp(a, b, offsetof(ui_state_t, jig_x)) ||
        memcmp(&a->jig_next_s, &b->jig_next_s, offsetof(ui_state_t, clip_src) - offsetof(ui_state_t, jig_next_s)) ||
        // and the numbers appended after the clip (the Jiggler settings, firmware 1.8.0)
        memcmp(&a->jig_menu_on, &b->jig_menu_on, sizeof *a - offsetof(ui_state_t, jig_menu_on)))
        d |= UI_SYNC_FIELDS;
    if ((int)a->jig_x != (int)b->jig_x || (int)a->jig_y != (int)b->jig_y) d |= UI_SYNC_DOT;
    if (strncmp(a->msg, b->msg, sizeof a->msg)) d |= UI_SYNC_MSG;
    if (strncmp(a->clip_src, b->clip_src, sizeof a->clip_src)) d |= UI_SYNC_SRC;
    if (strncmp(a->bt_host, b->bt_host, sizeof a->bt_host)) d |= UI_SYNC_HOST;
    if (strncmp(a->down_reason, b->down_reason, sizeof a->down_reason)) d |= UI_SYNC_DOWN;
    return d;
}

void ui_sync_commit_fields(ui_state_t *prev, const ui_state_t *cur) {
    memcpy(prev, cur, offsetof(ui_state_t, clip_src));
    // and the numbers appended after the clip (the Jiggler settings, firmware 1.8.0)
    memcpy(&prev->jig_menu_on, &cur->jig_menu_on, sizeof *cur - offsetof(ui_state_t, jig_menu_on));
}

int ui_sync_state_line(const ui_state_t *s, char *out, int n) {
    int li = s->jig_letter >= 0 && s->jig_letter < JIG_PATH_COUNT ? s->jig_letter : 0;
    // The first eight fields are the 1.6.0 STATE, which older apps parse.
    return snprintf(out, n,
                    "STATE jig=%d letter=%c scale=%d phase=%d x=%d y=%d clip=%d paste=%d "
                    "page=%d sub=%d t=%d pc=%d link=%d mute=%d timer=%d cst=%d ppos=%d paused=%d demo=%d "
                    "next=%d up=%d menus=%lu mode=%d bta=%d bts=%d btr=%d left=%d pk=%lu "
                    "jmenu=%d jkey=%d jopen=%d jpause=%d",
                    (int)s->jig_on, JIG_PATHS[li].name, (int)s->jig_scale, (int)s->jig_phase, (int)s->jig_x,
                    (int)s->jig_y, (int)s->clip_len, s->clip_state == CLIP_PASTING, (int)s->screen, (int)s->sub,
                    (int)s->time_s, (int)s->helper, (int)s->link_ok, (int)s->muted, (int)s->timer_s,
                    (int)s->clip_state, (int)s->paste_pos, (int)s->jig_paused, (int)s->jig_demo, (int)s->jig_next_s,
                    (int)s->jig_up_s, (unsigned long)s->jig_menus, (int)s->bt_mode, (int)s->bt_avail,
                    (int)s->bt_state, (int)s->bt_ready, (int)s->bt_secs_left, (unsigned long)s->bt_passkey,
                    (int)s->jig_menu_on, (int)s->jig_key, (int)s->jig_open_s, (int)s->jig_pause_s);
}

int ui_sync_text_line(const char *key, const char *value, char *out, int n) {
    int len = snprintf(out, n, "TEXT %s %s", key, value);
    int end = len < n ? len : n - 1;
    for (int i = 5; i < end; i++)
        if ((unsigned char)out[i] < 0x20 || (unsigned char)out[i] > 0x7E) out[i] = '?';
    return end;
}

int ui_sync_clip_line(const char *clip, int len, char *out, int n) {
    static const char hex[] = "0123456789ABCDEF";
    int o = snprintf(out, n, "CLIPTEXT ");
    if (len > UI_CLIP_VIEW) len = UI_CLIP_VIEW;
    for (int i = 0; i < len && o < n - 5; i++) {
        unsigned char c = (unsigned char)clip[i];
        if (c == '\\') { out[o++] = '\\'; out[o++] = '\\'; }
        else if (c == '\n') { out[o++] = '\\'; out[o++] = 'n'; }
        else if (c == '\r') { out[o++] = '\\'; out[o++] = 'r'; }
        else if (c == '\t') { out[o++] = '\\'; out[o++] = 't'; }
        else if (c < 0x20 || c > 0x7E || (c == ' ' && i == len - 1)) {   // a last space would look trimmed
            out[o++] = '\\'; out[o++] = 'x'; out[o++] = hex[c >> 4]; out[o++] = hex[c & 15];
        } else out[o++] = (char)c;
    }
    out[o] = 0;
    return o;
}
