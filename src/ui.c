// core1: the frame loop. What to draw comes from ui_pages.c (shared with the
// PC app's device mirror); this file decides when, samples the shared state
// into a ui_state_t, and pushes frames or dirty rectangles to the LCD.
#include <string.h>
#include "pico/stdlib.h"
#include "pico/flash.h"
#include "app.h"
#include "board.h"
#include "gfx.h"
#include "lcd.h"
#include "ui.h"
#include "ui_pages.h"

// One consistent sample of the shared state. The clip and message are copied
// under clip_mtx, so drawing never holds the lock. with_clip = 0 skips the
// clip text (the mirror sync only needs its length).
void ui_state_fill(ui_state_t *s, int with_clip) {
    uint32_t now = now_ms();
    memset(s, 0, sizeof *s);
    s->screen = app.screen;
    s->time_s = app.time_s;
    s->helper = app.helper;
    s->link_ok = app.usb_mounted;
    s->muted = app.muted;
    s->timer_s = app.timer_s;
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
    s->jig_next_s = (int)((int32_t)(app.jig_next_menu_ms - now) / 1000);
    s->jig_up_s = s->jig_on ? (int32_t)((now - app.jig_started_ms) / 1000) : 0;
    s->jig_menus = app.jig_menus;
    mutex_enter_blocking(&clip_mtx);
    s->clip_len = app.clip_len;
    strncpy(s->clip_src, app.clip_src, sizeof s->clip_src - 1);
    if ((int32_t)(app.msg_until_ms - now) > 0) strncpy(s->msg, app.msg, sizeof s->msg - 1);
    if (with_clip) memcpy(s->clip, app.clip, s->clip_len < UI_CLIP_VIEW ? s->clip_len : UI_CLIP_VIEW);
    mutex_exit(&clip_mtx);
}

// ---------- frame loop ----------

static ui_state_t st;   // what this frame draws

// Regions that change on an animation frame (everything else is redrawn only
// when core0 bumps redraw_seq): the jiggler's dot (a small box around its old
// and new positions) plus its status lines, or the clipboard's progress line.
static const rect_t JIG_STATUS = {0, 206, LCD_W, 44};
static const rect_t CLIP_ANIM = {0, CLIP_STATUS_Y - 10, LCD_W, 20};

static bool msg_showing(void) { return (int32_t)(app.msg_until_ms - now_ms()) > 0; }

static rect_t clamp_to_screen(rect_t r) {
    if (r.x < 0) { r.w += r.x; r.x = 0; }
    if (r.y < 0) { r.h += r.y; r.y = 0; }
    if (r.x + r.w > LCD_W) r.w = LCD_W - r.x;
    if (r.y + r.h > LCD_H) r.h = LCD_H - r.y;
    return r;
}

// Sample the state for `screen` at clock time t.
static void sample(int screen, int t) {
    ui_state_fill(&st, screen == SCR_CLIP);
    st.screen = screen;
    st.time_s = t;
}

// Draw the watch page for time t clipped to r (dial and ticks underneath come
// back through the clip).
static void draw_watch_region(rect_t r, int t) {
    sample(SCR_WATCH, t);
    gfx_set_clip(r.x, r.y, r.w, r.h);
    ui_draw_page(&st);
    gfx_clip_reset();
}

void ui_core1_main(void) {
    // Lets core0 park this core while it writes settings to flash.
    flash_safe_execute_core_init();

    uint32_t drawn_seq = ~0u, last_frame = 0, drawn_wtick = 0, seen_edge = 0;
    int drawn_screen = -1;
    bool first = true, msg_shown = false;

    // Watch page: `shown_t` is the second on screen. Right after it is shown,
    // the frame for the next second is drawn into fb ahead of time (`ready`),
    // then pushed the moment core0's second alarm fires, so the hands move
    // exactly with the tick.
    int shown_t = -1, ready_t = -1;
    bool ready = false;
    rect_t ready_rect = {0, 0, 0, 0};
    rect_t prev_hands = {0, 0, LCD_W, LCD_H};   // where the hands are on screen
    rect_t prev_dot = {0, 0, 0, 0};             // where the jiggler's dot is on screen
    uint32_t status_ms = 0;                      // jiggler status lines: last redraw (countdown text)

    for (;;) {
        int screen = app.screen;
        bool on_watch = screen == SCR_WATCH;
        uint32_t seq = app.redraw_seq, wtick = app.watch_tick, edge = app.second_edge;
        bool anim_jig = screen == SCR_JIG && (app.jig_on || app.anim_demo);
        bool anim_clip = screen == SCR_CLIP && app.clip_state == CLIP_PASTING;
        bool msg = msg_showing();
        bool full = seq != drawn_seq || screen != drawn_screen || msg != msg_shown;
        bool edge_new = on_watch && edge != seen_edge;
        bool stopwatch = on_watch && wtick != drawn_wtick;
        bool prerender = on_watch && !ready && shown_t == app.time_s;
        bool partial = (anim_jig || anim_clip) && now_ms() - last_frame >= 50;
        if (!full && !edge_new && !stopwatch && !prerender && !partial) {
            if (ready) sleep_us(200);    // a frame is waiting for its second: watch the edge closely
            else sleep_ms(2);
            continue;
        }
        drawn_seq = seq;
        drawn_screen = screen;
        msg_shown = msg;
        drawn_wtick = wtick;
        last_frame = now_ms();

        uint64_t t_draw = time_us_64(), t_push = t_draw;
        bool pushed = true;
        if (full) {
            int t = app.time_s;
            sample(screen, t);
            prev_dot = ui_dot_rect(st.jig_x, st.jig_y);
            ui_draw_page(&st);
            t_push = time_us_64();
            lcd_push_frame(fb);
            lcd_wait();
            seen_edge = edge;
            ready = false;
            if (on_watch) { shown_t = t; prev_hands = ui_hands_rect(t); }
        } else if (edge_new) {
            seen_edge = edge;
            int t = app.time_s;
            rect_t r;
            if (ready && ready_t == t) {             // drawn ahead: just send it
                r = ready_rect;
                app.prerender_hits++;
            } else {                                 // not ready in time: draw it now
                r = clamp_to_screen(ui_rect_union(ui_rect_union(prev_hands, ui_hands_rect(t)), ui_stopwatch_rect()));
                draw_watch_region(r, t);
                app.prerender_misses++;
            }
            bool hit = ready && ready_t == t;
            t_push = time_us_64();
            lcd_push_rect(fb, r.x, r.y, r.w, r.h);
            if (hit) {   // edge -> frame on screen, for drawn-ahead frames (misses are counted instead)
                uint32_t lag = time_us_32() - app.edge_us;
                app.perf_edge_lag_us += ((int32_t)lag - (int32_t)app.perf_edge_lag_us) / 4;
            }
            shown_t = t;
            prev_hands = ui_hands_rect(t);
            ready = false;
        } else if (stopwatch) {
            // The stopwatch ticks on its own schedule: repaint just its box for
            // the second on screen. That overwrites part of any drawn-ahead
            // frame, so draw the next one again afterwards.
            rect_t r = clamp_to_screen(ui_stopwatch_rect());
            draw_watch_region(r, shown_t);
            t_push = time_us_64();
            lcd_push_rect(fb, r.x, r.y, r.w, r.h);
            ready = false;
        } else if (prerender) {
            int next = (shown_t + 1) % 86400;
            ready_rect = clamp_to_screen(ui_rect_union(ui_rect_union(prev_hands, ui_hands_rect(next)), ui_stopwatch_rect()));
            draw_watch_region(ready_rect, next);
            ready_t = next;
            ready = true;
            pushed = false;
        } else {
            rect_t r[2];
            int n = 1;
            sample(screen, app.time_s);
            if (anim_jig) {                          // the dot's old+new box, and the status lines
                rect_t now = ui_dot_rect(st.jig_x, st.jig_y);
                r[0] = clamp_to_screen(ui_rect_union(prev_dot, now));
                prev_dot = now;
                if (now_ms() - status_ms >= 250) {   // the countdown text changes once a second
                    r[n++] = JIG_STATUS;
                    status_ms = now_ms();
                }
            } else {
                r[0] = CLIP_ANIM;
            }
            for (int i = 0; i < n; i++) {           // draw each region clipped, then send just it
                gfx_set_clip(r[i].x, r[i].y, r[i].w, r[i].h);
                ui_draw_page(&st);
            }
            gfx_clip_reset();
            t_push = time_us_64();
            for (int i = 0; i < n; i++) lcd_push_rect(fb, r[i].x, r[i].y, r[i].w, r[i].h);
        }
        // Frame timing for DBG: exponential averages (1/8) and the worst draw seen.
        uint64_t t_end = time_us_64();
        uint32_t draw_us = (uint32_t)((pushed ? t_push : t_end) - t_draw), push_us = (uint32_t)(t_end - t_push);
        app.perf_draw_us += ((int32_t)draw_us - (int32_t)app.perf_draw_us) / 8;
        if (pushed) app.perf_push_us += ((int32_t)push_us - (int32_t)app.perf_push_us) / 8;
        if (draw_us > app.perf_draw_max_us) app.perf_draw_max_us = draw_us;
        if (pushed) app.frames++;
        if (first) { lcd_set_backlight(80); first = false; }
    }
}
