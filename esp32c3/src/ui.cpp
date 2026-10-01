// Render task: the frame loop. What to draw comes from ui_pages.c (shared with
// the PC app's device mirror); this file decides when, samples the shared state
// into a ui_state_t, and pushes frames or dirty rectangles to the panel.
#include <string.h>
#include "app.h"
#include "ble_hid.h"
#include "board.h"
#include "display.h"
#include "gfx.h"
#include "output.h"
#include "ui.h"
#include "ui_pages.h"

static_assert(UI_BT_UNPAIRED == (int)BT_UNPAIRED && UI_BT_PAIRING == (int)BT_PAIRING &&
              UI_BT_WAITING == (int)BT_WAITING && UI_BT_CONNECTED == (int)BT_CONNECTED && UI_BT_OFF == (int)BT_OFF,
              "ui_pages.h mirrors bt_state_t");

// One consistent sample of the shared state. The clip and message are copied
// under clip_mtx, so drawing never holds the lock. with_clip = false skips the
// clip text (the mirror sync only needs its length).
void ui_state_fill(ui_state_t *s, bool with_clip) {
    uint32_t now = now_ms();
    memset(s, 0, sizeof *s);
    s->screen = app.screen;
    s->sub = app.in_bt ? UI_SUB_BT : app.jig_settings && app.screen == SCR_JIG ? UI_SUB_JIGSET : UI_SUB_NONE;
    s->time_s = app.time_s;
    s->timer_s = app.timer_s;
    s->helper = app.helper;
    s->link_ok = out_ready();
    s->bt_mode = mode_get() == MODE_BT;
    s->bt_avail = mode_bt_available();
    s->bt_state = ble_state();
    s->bt_ready = ble_ready();
    s->bt_secs_left = ble_pair_secs_left();
    s->bt_passkey = ble_passkey();
    s->clip_state = app.clip_state;
    s->paste_pos = app.paste_pos;
    s->jig_on = app.jig_on;
    s->jig_demo = app.anim_demo;
    s->jig_paused = app.jig_paused;
    s->jig_phase = app.jig_phase;
    s->jig_letter = app.jig_letter;
    s->jig_scale = app.jig_scale_idx;
    s->jig_x = app.jig_x;
    s->jig_y = app.jig_y;
    s->jig_next_s = s->jig_on ? (int)((int32_t)(app.jig_next_menu_ms - now) / 1000) : 0;
    s->jig_up_s = s->jig_on ? (int32_t)((now - app.jig_started_ms) / 1000) : 0;
    s->jig_menus = app.jig_menus;
    s->jig_menu_on = app.jig_cfg.menu_on;
    s->jig_key = app.jig_cfg.key_f15;
    s->jig_open_s = app.jig_cfg.open_s;
    s->jig_pause_s = app.jig_cfg.pause_s;
    strncpy(s->bt_host, ble_host_name(), sizeof s->bt_host - 1);
    strncpy(s->down_reason, out_down_reason(), sizeof s->down_reason - 1);
    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    s->clip_len = app.clip_len;
    strncpy(s->clip_src, app.clip_src, sizeof s->clip_src - 1);
    if ((int32_t)(app.msg_until_ms - now) > 0) strncpy(s->msg, app.msg, sizeof s->msg - 1);
    if (with_clip) memcpy(s->clip, app.clip, s->clip_len < UI_CLIP_VIEW ? s->clip_len : UI_CLIP_VIEW);
    xSemaphoreGive(clip_mtx);
}

static ui_state_t st;   // what this frame draws

// Frames: a full redraw when the logic task bumps redraw_seq, once a second on
// the jiggler page (countdown) and every 100 ms while copying/pasting. While the
// jiggler dot moves, only its old+new box (and the status lines every 250 ms)
// is redrawn and pushed, 20 times a second: the letter lanes are costly to draw.
void ui_task(void *) {
    static const rect_t JIG_STATUS = {0, 180, LCD_W, 34};
    uint32_t drawn_seq = ~0u, last_full = 0, last_part = 0, status_ms = 0;
    rect_t prev_dot = {0, 0, 0, 0};
    int drawn_screen = -1, drawn_time = -2, drawn_timer = -1;
    bool first = true;
    for (;;) {
        int screen = app.screen;
        bool in_bt = app.in_bt;
        uint32_t seq = app.redraw_seq, now = now_ms();
        bool anim = !in_bt && !app.jig_settings && screen == SCR_JIG && (app.jig_on || app.anim_demo);
        uint32_t period = app.clip_state != CLIP_IDLE ? 100 : (screen == SCR_JIG ? 1000 : 500);
        // The watch redraws exactly when its second (or the stopwatch) changes, not on a timer.
        bool watch_tick = screen == SCR_WATCH && !in_bt && (app.time_s != drawn_time || app.timer_s != drawn_timer);
        if (screen == SCR_WATCH) period = 5000;
        bool full = seq != drawn_seq || screen != drawn_screen || now - last_full >= period || watch_tick;
        bool part = anim && now - last_part >= 50;
        if (!full && !part) {
            vTaskDelay(pdMS_TO_TICKS(5));
            continue;
        }
        ui_state_fill(&st, !in_bt && screen == SCR_CLIP);
        st.screen = screen;
        st.sub = in_bt ? UI_SUB_BT : app.jig_settings && screen == SCR_JIG ? UI_SUB_JIGSET : UI_SUB_NONE;
        rect_t dot = ui_dot_rect(st.jig_x, st.jig_y);
        if (full) {
            drawn_seq = seq;
            drawn_screen = screen;
            drawn_time = app.time_s;
            drawn_timer = app.timer_s;
            last_full = last_part = status_ms = now;
            ui_draw_page(&st);
            display_push();
            display_wait();
        } else {
            last_part = now;
            rect_t r[2] = {ui_rect_union(prev_dot, dot), JIG_STATUS};
            int n = 1;
            if (now - status_ms >= 250) { n = 2; status_ms = now; }
            for (int i = 0; i < n; i++) {
                gfx_set_clip(r[i].x, r[i].y, r[i].w, r[i].h);
                ui_draw_page(&st);
            }
            gfx_clip_reset();
            for (int i = 0; i < n; i++) display_push_rect(r[i].x, r[i].y, r[i].w, r[i].h);
        }
        prev_dot = dot;
        app.frames++;
        if (first) { display_backlight(200); first = false; }
    }
}
