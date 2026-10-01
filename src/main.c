// Touch Deck: watch face, device-memory clipboard (types text over USB HID),
// and a mouse jiggler. Swipe left/right to move between screens.
//
// core0: USB (HID + CDC), touch input, typing and jiggler state machines.
// core1: rendering (ui.c), so slow frames never delay USB reports.
#include <stdio.h>
#include <string.h>
#include "pico/stdlib.h"
#include "pico/multicore.h"
#include "hardware/clocks.h"
#include "hardware/vreg.h"
#include "tusb.h"
#include "app.h"
#include "board.h"
#include "button.h"
#include "jig_paths.h"
#include "jiggler.h"
#include "lcd.h"
#include "settings.h"
#include "touch.h"
#include "typer.h"
#include "ui.h"
#include "usb_io.h"

#ifdef TD_ROUND
// Round board (RP2350-Touch-LCD-1.28): a silent round watch (no buzzer, so no tick
// and no mute toggle), Clipboard and Jiggler. No power latch.
#define MUTE_HIT_X LCD_W         // nothing to tap: no mute on a silent watch
#define MUTE_HIT_Y 0
static inline void buzzer_init(void) {}
static inline void buzzer_tick(void) {}
static inline void buzzer_tone(uint32_t f, uint32_t ms, uint32_t duty) { (void)f; (void)ms; (void)duty; }
#define START_SCREEN SCR_WATCH
#else
#include "buzzer.h"
#define START_SCREEN SCR_WATCH
#endif
#ifndef UI_PAGE_COUNT
#define UI_PAGE_COUNT SCR_COUNT
#endif

#define TOUCH_POLL_MS 2
#define BUTTON_POLL_MS 20
#define COPY_TIMEOUT_MS 3000

app_t app;
mutex_t clip_mtx;

static alarm_id_t second_alarm;
static uint32_t copy_deadline;
static uint64_t timer_accum_us, timer_start_us;   // stopwatch: banked time + current run

uint32_t now_ms(void) { return to_ms_since_boot(get_absolute_time()); }

void app_message(const char *text) {
    mutex_enter_blocking(&clip_mtx);
    strncpy(app.msg, text, sizeof app.msg - 1);
    app.msg[sizeof app.msg - 1] = 0;
    app.msg_until_ms = now_ms() + 2500;
    mutex_exit(&clip_mtx);
    app_redraw();
}

// The clock's second, from a hardware timer alarm (IRQ on core0). Returning
// 1 s re-arms it relative to when it was *scheduled* to fire, so it never
// drifts. The tick plays right here, exactly on the second; core1 has usually
// drawn this second's frame already and pushes it when it sees the edge.
static int64_t on_second(alarm_id_t id, void *user) {
    (void)id; (void)user;
    app.time_s = (app.time_s + 1) % 86400;
    app.edge_us = time_us_32();
    app.second_edge++;
    if (app.screen == SCR_WATCH && !app.muted) buzzer_tick();
    return 1000000;
}

// TIME from the helper (or the compile time at boot) sets the clock; its
// second boundary starts now.
void clock_set(int seconds_of_day) {
    if (second_alarm > 0) cancel_alarm(second_alarm);
    app.time_s = seconds_of_day;
    second_alarm = add_alarm_in_us(1000000, on_second, NULL, true);
    app_redraw();
}

// Stopwatch on the watch face. timer_s is what the face shows; a change on
// the watch page requests a redraw (independently of the clock's second).
static void timer_update(void) {
    uint64_t us = timer_accum_us + (app.timer_running ? time_us_64() - timer_start_us : 0);
    int s = (int)(us / 1000000);
    if (s != app.timer_s) {
        app.timer_s = s;
        if (app.screen == SCR_WATCH) app.watch_tick++;   // hands/stopwatch only: partial redraw
    }
}

static void timer_toggle(void) {
    if (app.timer_running) timer_accum_us += time_us_64() - timer_start_us;
    else timer_start_us = time_us_64();
    app.timer_running = !app.timer_running;
    app_redraw();
}

static void timer_reset(void) {
    app.timer_running = false;
    timer_accum_us = 0;
    app.timer_s = 0;
    app_redraw();
}

// __TIME__ is "HH:MM:SS"; used until the PC helper sends the real time.
static int compile_seconds(void) {
    const char *t = __TIME__;
    return ((t[0] - '0') * 10 + (t[1] - '0')) * 3600 +
           ((t[3] - '0') * 10 + (t[4] - '0')) * 60 +
           ((t[6] - '0') * 10 + (t[7] - '0'));
}

// Answer to the helper's DBG command: one line of liveness + touch state.
// Deliberately does not read the touch chip (unsolicited reads wedge it).
void debug_report(void) {
    char s[448];
    snprintf(s, sizeof s,
             "LOG up=%lus loops=%lu frames=%lu screen=%d muted=%d | touch chip=%d ints=%u reads=%u "
             "fails=%u recoveries=%u presses=%u events=%u xy=%d,%d lines=%d | jig=%d jscale=%.1f letter=%c clip=%d jnext=%d timer=%d trun=%d "
             "jmenu=%d jkey=%d jopen=%d jpause=%d jset=%d | draw=%lu push=%lu drawmax=%lu lag=%lu hits=%lu miss=%lu",
             (unsigned long)(now_ms() / 1000), (unsigned long)app.loops, (unsigned long)app.frames,
             app.screen, app.muted, touch_stats.chip_id, touch_stats.ints, touch_stats.reads,
             touch_stats.fails, touch_stats.recoveries, touch_stats.presses, touch_stats.events,
             touch_stats.last_x, touch_stats.last_y, touch_diag_lines(),
             app.jig_on, (double)JIG_SCALES[app.jig_scale_idx], JIG_PATHS[app.jig_letter].name, app.clip_len, jiggler_next_menu_s(), app.timer_s, app.timer_running,
             app.jig_cfg.menu_on, app.jig_cfg.key_f15, app.jig_cfg.open_s, app.jig_cfg.pause_s, app.jig_settings,
             (unsigned long)app.perf_draw_us, (unsigned long)app.perf_push_us,
             (unsigned long)app.perf_draw_max_us, (unsigned long)app.perf_edge_lag_us,
             (unsigned long)app.prerender_hits, (unsigned long)app.prerender_misses);
    app.perf_draw_max_us = 0;   // worst case since the previous DBG
    usb_send_line(s);
}

static void feedback(void) {
    buzzer_tone(2500, 4, 15);   // touch/button click: always on (mute silences only the watch tick)
}

static bool in_rect(const touch_event_t *e, int x, int y, int w, int h) {
    return e->x >= x && e->x < x + w && e->y >= y && e->y < y + h;
}

static bool near_box(const touch_event_t *e, int x, int y, int w, int h, int pad) {
    return in_rect(e, x - pad, y - pad, w + 2 * pad, h + 2 * pad);
}

// The 1.69's touch panel squeezes its top band: a finger on something drawn at y 42
// reports y ~2 (measured on the board), while 40 px lower it is only a few px off.
// So a target near the top also takes taps above it, up to the edge (nothing else
// up there is touchable).
#define TOP_BAND 64
static bool near_box_top(const touch_event_t *e, int x, int y, int w, int h, int pad) {
    int y0 = y - pad;
    if (y0 < TOP_BAND) return in_rect(e, x - pad, 0, w + 2 * pad, y + h + pad);
    return near_box(e, x, y, w, h, pad);
}

// Trash can / CLIP CLEAR: only with text on board and no paste typing it.
void clip_clear(void) {
    if (app.clip_len == 0 || app.clip_state == CLIP_PASTING) return;
    mutex_enter_blocking(&clip_mtx);
    app.clip_len = 0;
    app.clip[0] = 0;
    app.clip_seq++;
    strcpy(app.clip_src, "-");
    mutex_exit(&clip_mtx);
    app_message("Cleared");
    app_redraw();
}

// Jiggler settings page and JIG CFG. Applies from the next menu event; saved ~1 s
// after the last change, so a run of -/+ taps writes flash once.
void jig_set_cfg(const jig_cfg_t *c) {
    jig_cfg_t n = *c;
    jmenu_clamp(&n);
    app.jig_cfg = n;
    app_redraw();
    settings_save_soon();
}

// Scale pill tap, BOOT button on the jiggler page, JIG SCALE: 1x -> 1.5x -> 2x -> 1x.
void jig_cycle_scale(void) {
    app.jig_scale_idx = (app.jig_scale_idx + 1) % JIG_SCALE_COUNT;
    app_redraw();
    settings_save();
}

// Jiggler settings page: each tap changes one setting, applied from the next menu
// event and saved. "Menu open" ignores taps while the context menu is off.
static void on_jigset_tap(const touch_event_t *e) {
    jig_cfg_t c = app.jig_cfg;
    const int h = JS_CTRL_H, pad = JS_HIT_PAD;
    int r0 = JS_ROW_Y(0) - h / 2, r1 = JS_ROW_Y(1) - h / 2, r2 = JS_ROW_Y(2) - h / 2, r3 = JS_ROW_Y(3) - h / 2;
    if (near_box(e, JS_TOGGLE_X, r0, JS_TOGGLE_W, h, pad)) {
        c.menu_on = !c.menu_on;
    } else if (near_box(e, JS_SEG_ESC_X, r1, JS_SEG_F15_X + JS_SEG_W - JS_SEG_ESC_X, h, pad)) {
        c.key_f15 = e->x >= JS_SEG_F15_X;      // the two pills sit 2 px apart: split at F15's edge
    } else if (c.menu_on && near_box(e, JS_MINUS_X, r2, JS_STEP_W, h, pad)) {
        if (c.open_s > 0) c.open_s--;
    } else if (c.menu_on && near_box(e, JS_PLUS_X, r2, JS_STEP_W, h, pad)) {
        if (c.open_s < JM_MAX_S) c.open_s++;
    } else if (near_box(e, JS_MINUS_X, r3, JS_STEP_W, h, pad)) {
        if (c.pause_s > 0) c.pause_s--;
    } else if (near_box(e, JS_PLUS_X, r3, JS_STEP_W, h, pad)) {
        if (c.pause_s < JM_MAX_S) c.pause_s++;
    } else {
        return;
    }
    feedback();
    jig_set_cfg(&c);
}

static void set_jig_settings(bool open) {
    app.jig_settings = open;
    app_redraw();
}

static void on_touch(touch_event_t e) {
    // The Jiggler settings panel: X or a right swipe closes it; taps change settings.
    if (app.jig_settings && app.screen == SCR_JIG) {
        if (e.type == EV_SWIPE_R ||
            (e.type == EV_TAP && near_box_top(&e, JS_CLOSE_CX - JS_ICON_HIT, JS_CLOSE_CY - JS_ICON_HIT,
                                          2 * JS_ICON_HIT, 2 * JS_ICON_HIT, 0))) {
            feedback();
            set_jig_settings(false);
        } else if (e.type == EV_TAP) {
            on_jigset_tap(&e);
        }
        return;
    }
    switch (e.type) {
    case EV_SWIPE_L:
        if (app.screen < UI_PAGE_COUNT - 1) { app.screen++; app_redraw(); feedback(); }
        return;
    case EV_SWIPE_R:
        if (app.screen > 0) { app.screen--; app_redraw(); feedback(); }
        return;
    case EV_TAP:
        break;
    default:
        return;
    }

    if (app.screen == SCR_WATCH) {
        if (e.x >= MUTE_HIT_X && e.y < MUTE_HIT_Y) {
            app.muted = !app.muted;
            feedback();
            app_redraw();
            settings_save();
        }
    } else if (app.screen == SCR_CLIP) {
        if (near_box_top(&e, TRASH_CX - TRASH_HIT, TRASH_CY - TRASH_HIT, 2 * TRASH_HIT, 2 * TRASH_HIT, 0)) {
            if (app.clip_len && app.clip_state != CLIP_PASTING) { feedback(); clip_clear(); }
        } else if (in_rect(&e, BTN_COPY_X, BTN_Y, BTN_W, BTN_H)) {
            feedback();
            if (app.clip_state == CLIP_PASTING) return;
            if (!app.helper) { app_message("Start the PC helper"); return; }
            app.clip_state = CLIP_COPYING;
            copy_deadline = now_ms() + COPY_TIMEOUT_MS;
            usb_send_line("COPY");
            app_redraw();
        } else if (in_rect(&e, BTN_PASTE_X, BTN_Y, BTN_W, BTN_H)) {
            feedback();
            if (typer_busy()) typer_cancel();
            else if (app.clip_len) typer_start();
            app_redraw();
        }
    } else if (app.screen == SCR_JIG) {
        const int pad = (int)(JIG_LANE / 2 + JIG_WALL) + JIG_ZONE_PAD;
        if (near_box(&e, JIG_COG_CX - JS_ICON_HIT, JIG_COG_CY - JS_ICON_HIT, 2 * JS_ICON_HIT, 2 * JS_ICON_HIT, 0)) {
            feedback();
            set_jig_settings(true);
        } else if (near_box_top(&e, SCALE_PILL_X, PILL_Y, PILL_W, PILL_H, PILL_PAD)) {
            feedback();
            jig_cycle_scale();
        } else if (near_box_top(&e, ONOFF_PILL_X, PILL_Y, PILL_W, PILL_H, PILL_PAD) ||
                   near_box(&e, JIG_BOX_X, JIG_BOX_Y, JIG_BOX, JIG_BOX, pad)) {
            feedback();
            jiggler_toggle();
            settings_save();
        }
    }
}

// The BOOT button: the one input every Touch Deck board has.
static void on_button(btn_ev_t ev) {
    if (app.screen == SCR_WATCH) {
        if (ev == BTN_SHORT) timer_toggle();          // start / pause the stopwatch
        else timer_reset();                           // long press: back to 00:00:00
        feedback();
    } else if (app.screen == SCR_JIG && ev == BTN_SHORT) {
        feedback();
        jig_cycle_scale();
    }
}

// Serial TAP-free scripting (tests): SWIPE L|R and BTN / BTN LONG land here.
void inject_swipe(bool left) {
    touch_event_t e = {left ? EV_SWIPE_L : EV_SWIPE_R, 120, 140};
    on_touch(e);
}

void inject_button(bool long_press) { on_button(long_press ? BTN_LONG : BTN_SHORT); }

void inject_tap(int x, int y) {
    touch_event_t e = {EV_TAP, x, y};
    on_touch(e);
}

int main(void) {
    // Hold the power latch first so the board stays on when running from battery.
#ifdef SYS_EN_PIN
    gpio_init(SYS_EN_PIN);
    gpio_set_dir(SYS_EN_PIN, GPIO_OUT);
    gpio_put(SYS_EN_PIN, 1);
#endif

    // 200 MHz instead of 125: drawing is pure software maths (no FPU), so it
    // scales with the clock. 1.15 V is the usual core voltage for this speed.
    // Peripherals re-derive their rates from the new clock (SPI lands at 50 MHz).
    vreg_set_voltage(VREG_VOLTAGE_1_15);
    sleep_ms(2);
    set_sys_clock_khz(200000, true);

    mutex_init(&clip_mtx);
    strcpy(app.clip_src, "-");
    app.screen = START_SCREEN;
    settings_load();
    clock_set(compile_seconds());

    lcd_init();
    buzzer_init();
    touch_init();
    usb_io_init();
    button_init();   // before core1 starts: see button.c
    // Resume jiggling if it was on at power-off. It waits for USB to be
    // mounted before moving anything.
    if (settings_jig_on()) jiggler_set(true);
    multicore_launch_core1(ui_core1_main);

    uint32_t next_touch = 0, next_button = 0;
    for (;;) {
        app.loops++;
        bool mounted = tud_mounted();
        if (mounted != app.usb_mounted) { app.usb_mounted = mounted; app_redraw(); }   // chip dot
        usb_io_poll();
        usb_state_poll();
        settings_poll();          // a debounced settings write, when due
        timer_update();

        if ((int32_t)(now_ms() - next_touch) >= 0) {
            next_touch = now_ms() + TOUCH_POLL_MS;
            touch_event_t e = touch_poll();
            if (e.type != EV_NONE) {
                touch_stats.events++;
                char s[48];
                snprintf(s, sizeof s, "LOG ev=%d at %d,%d", e.type, e.x, e.y);
                usb_send_line(s);
                on_touch(e);
            }
        }

        if ((int32_t)(now_ms() - next_button) >= 0) {
            next_button = now_ms() + BUTTON_POLL_MS;
            btn_ev_t b = button_poll();
            if (b != BTN_NONE) on_button(b);
        }

        if (app.clip_state == CLIP_COPYING && (int32_t)(now_ms() - copy_deadline) >= 0) {
            app.clip_state = CLIP_IDLE;
            app_message("No reply from PC");
        }

        typer_step();
        jiggler_step();

    }
}
