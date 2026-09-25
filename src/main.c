// Touch Deck: watch face, device-memory clipboard (types text over USB HID),
// and a mouse jiggler. Swipe left/right to move between screens.
//
// core0: USB (HID + CDC), touch input, typing and jiggler state machines.
// core1: rendering (ui.c), so slow frames never delay USB reports.
#include <stdio.h>
#include <string.h>
#include "pico/stdlib.h"
#include "pico/multicore.h"
#include "app.h"
#include "board.h"
#include "buzzer.h"
#include "jiggler.h"
#include "lcd.h"
#include "settings.h"
#include "touch.h"
#include "typer.h"
#include "ui.h"
#include "usb_io.h"

#define TOUCH_POLL_MS 2
#define COPY_TIMEOUT_MS 3000

app_t app;
mutex_t clip_mtx;

static int clock_base_s;
static uint64_t clock_base_us;
static uint32_t copy_deadline;

uint32_t now_ms(void) { return to_ms_since_boot(get_absolute_time()); }

void app_message(const char *text) {
    mutex_enter_blocking(&clip_mtx);
    strncpy(app.msg, text, sizeof app.msg - 1);
    app.msg[sizeof app.msg - 1] = 0;
    app.msg_until_ms = now_ms() + 2500;
    mutex_exit(&clip_mtx);
    app_redraw();
}

void clock_set(int seconds_of_day) {
    clock_base_s = seconds_of_day;
    clock_base_us = time_us_64();
    app_redraw();
}

static void clock_update(void) {
    int t = (int)((clock_base_s + (time_us_64() - clock_base_us) / 1000000) % 86400);
    if (t != app.time_s) {
        app.time_s = t;
        if (app.screen == SCR_WATCH) app_redraw();
    }
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
    char s[200];
    snprintf(s, sizeof s,
             "LOG up=%lus loops=%lu frames=%lu screen=%d muted=%d | touch chip=%d ints=%u reads=%u "
             "fails=%u recoveries=%u presses=%u events=%u xy=%d,%d lines=%d",
             (unsigned long)(now_ms() / 1000), (unsigned long)app.loops, (unsigned long)app.frames,
             app.screen, app.muted, touch_stats.chip_id, touch_stats.ints, touch_stats.reads,
             touch_stats.fails, touch_stats.recoveries, touch_stats.presses, touch_stats.events,
             touch_stats.last_x, touch_stats.last_y, touch_diag_lines());
    usb_send_line(s);
}

static void feedback(void) {
    if (!app.muted) buzzer_tone(2500, 4, 15);
}

static bool in_rect(const touch_event_t *e, int x, int y, int w, int h) {
    return e->x >= x && e->x < x + w && e->y >= y && e->y < y + h;
}

static void on_touch(touch_event_t e) {
    switch (e.type) {
    case EV_SWIPE_L:
        if (app.screen < SCR_COUNT - 1) { app.screen++; app_redraw(); feedback(); }
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
            feedback();   // audible confirmation only when un-muting
            app_redraw();
            settings_save();
        }
    } else if (app.screen == SCR_CLIP) {
        if (in_rect(&e, BTN_COPY_X, BTN_Y, BTN_W, BTN_H)) {
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
        int dx = e.x - JIG_CX, dy = e.y - JIG_CY;
        if (dx * dx + dy * dy <= (JIG_R + 10) * (JIG_R + 10)) {
            feedback();
            jiggler_toggle();
            settings_save();
        }
    }
}

int main(void) {
    // Hold the power latch first so the board stays on when running from battery.
    gpio_init(SYS_EN_PIN);
    gpio_set_dir(SYS_EN_PIN, GPIO_OUT);
    gpio_put(SYS_EN_PIN, 1);

    mutex_init(&clip_mtx);
    strcpy(app.clip_src, "-");
    app.screen = SCR_WATCH;
    settings_load();
    clock_set(compile_seconds());
    app.time_s = compile_seconds();

    lcd_init();
    buzzer_init();
    touch_init();
    usb_io_init();
    // Resume jiggling if it was on at power-off. It waits for USB to be
    // mounted before moving anything.
    if (settings_jig_on()) jiggler_set(true);
    multicore_launch_core1(ui_core1_main);

    uint32_t next_touch = 0;
    for (;;) {
        app.loops++;
        usb_io_poll();
        clock_update();

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

        if (app.clip_state == CLIP_COPYING && (int32_t)(now_ms() - copy_deadline) >= 0) {
            app.clip_state = CLIP_IDLE;
            app_message("No reply from PC");
        }

        typer_step();
        jiggler_step();

        if (app.tick_pending) {
            app.tick_pending = false;
            if (!app.muted) buzzer_tick();
        }
    }
}
