// Touch Deck for the ESP32-2424S012C: device-memory clipboard that types its
// text over Bluetooth, and a Bluetooth mouse jiggler. Swipe left/right to move
// between the two screens. The PC helper (tools/clip_helper.py) talks to it
// over the USB cable, same protocol as the RP2040 version.
//
// Arduino loop (priority raised to 2): serial link, touch, typer, jiggler.
// ui_task (priority 1): rendering only, so slow frames never delay HID reports.
#include <Arduino.h>
#include <Preferences.h>
#include "app.h"
#include "ble_hid.h"
#include "display.h"
#include "jiggler.h"
#include "link.h"
#include "touch.h"
#include "typer.h"
#include "ui.h"

#define COPY_TIMEOUT_MS 3000

app_t app;
SemaphoreHandle_t clip_mtx;

static Preferences prefs;
static int clock_base_s = -1;
static uint32_t clock_base_ms, copy_deadline;

void app_message(const char *text) {
    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    strncpy(app.msg, text, sizeof app.msg - 1);
    app.msg[sizeof app.msg - 1] = 0;
    app.msg_until_ms = now_ms() + 2500;
    xSemaphoreGive(clip_mtx);
    app_redraw();
}

void clock_set(int seconds_of_day) {
    clock_base_s = seconds_of_day;
    clock_base_ms = now_ms();
    app_redraw();
}

static void clock_update() {
    if (clock_base_s < 0) return;
    int t = (int)((clock_base_s + (now_ms() - clock_base_ms) / 1000) % 86400);
    if (t / 60 != app.time_s / 60) app_redraw();   // header shows HH:MM
    app.time_s = t;
}

// Answer to the helper's DBG command. Never reads the touch chip.
void debug_report() {
    char s[300];
    snprintf(s, sizeof s,
             "LOG up=%lus frames=%lu screen=%d bt_page=%d heap=%u | ble state=%d conn=%d ready=%d host=%s | touch chip=%d ints=%u "
             "reads=%u fails=%u recoveries=%u presses=%u events=%u xy=%d,%d lines=%d",
             (unsigned long)(now_ms() / 1000), (unsigned long)app.frames, app.screen, app.in_bt,
             (unsigned)ESP.getFreeHeap(), (int)ble_state(), ble_connected(), ble_ready(), ble_host_name(), touch_stats.chip_id,
             touch_stats.ints, touch_stats.reads, touch_stats.fails, touch_stats.recoveries,
             touch_stats.presses, touch_stats.events, touch_stats.last_x, touch_stats.last_y,
             touch_diag_lines());
    link_send_line(s);
}

static bool in_rect(const touch_event_t &e, int x, int y, int w, int h) {
    return e.x >= x && e.x < x + w && e.y >= y && e.y < y + h;
}

static void on_touch_bt(const touch_event_t &e) {
    if (e.type == EV_SWIPE_R || (e.type == EV_TAP && e.x < BACK_HIT_X && e.y < BACK_HIT_Y)) {
        app.in_bt = false;   // back up to Settings
        app_redraw();
        return;
    }
    if (e.type != EV_TAP || e.y < BT_BTN_Y || e.y >= BT_BTN_Y + BT_BTN_H) return;

    bt_state_t st = ble_state();
    if (st == BT_UNPAIRED || st == BT_PAIRING) {
        if (e.x >= BT_BTN1_X && e.x < BT_BTN1_X + BT_BTN1_W) {
            if (st == BT_UNPAIRED) ble_pair_start();
            else ble_pair_cancel();
        }
    } else if (in_rect(e, BTN_COPY_X, BT_BTN_Y, BTN_W, BT_BTN_H)) {
        if (st == BT_OFF) ble_reconnect();
        else ble_disconnect();
    } else if (in_rect(e, BTN_PASTE_X, BT_BTN_Y, BTN_W, BT_BTN_H)) {
        ble_forget();
    }
    app_redraw();
}

static void on_touch(const touch_event_t &e) {
    if (app.in_bt) { on_touch_bt(e); return; }

    switch (e.type) {
    case EV_SWIPE_L:
        if (app.screen < SCR_COUNT - 1) { app.screen++; app_redraw(); }
        return;
    case EV_SWIPE_R:
        if (app.screen > 0) { app.screen--; app_redraw(); }
        return;
    case EV_TAP:
        break;
    default:
        return;
    }

    if (app.screen == SCR_CLIP) {
        if (in_rect(e, BTN_COPY_X, BTN_Y, BTN_W, BTN_H)) {
            if (app.clip_state == CLIP_PASTING) return;
            if (!app.helper) { app_message("Start the PC helper"); return; }
            app.clip_state = CLIP_COPYING;
            copy_deadline = now_ms() + COPY_TIMEOUT_MS;
            link_send_line("COPY");
            app_redraw();
        } else if (in_rect(e, BTN_PASTE_X, BTN_Y, BTN_W, BTN_H)) {
            if (typer_busy()) typer_cancel();
            else if (app.clip_len) typer_start();
            app_redraw();
        }
    } else if (app.screen == SCR_JIG) {
        int dx = e.x - JIG_CX, dy = e.y - JIG_CY;
        if (dx * dx + dy * dy <= (JIG_R + 12) * (JIG_R + 12)) {
            jiggler_toggle();
            prefs.putBool("jig", app.jig_on);   // survives power-off
        }
    } else if (app.screen == SCR_SETTINGS) {
        int dx = e.x - COG_CX, dy = e.y - COG_CY;
        if (dx * dx + dy * dy <= COG_HIT_R * COG_HIT_R || (e.y > 140 && e.y < 205)) {
            app.in_bt = true;
            app_redraw();
        }
    }
}

void setup() {
    clip_mtx = xSemaphoreCreateMutex();
    link_init();
    display_init();
    touch_init();
    ble_init();

    prefs.begin("touchdeck", false);
    // Resume jiggling if it was on at power-off; it waits for Bluetooth.
    if (prefs.getBool("jig", false)) jiggler_set(true);

    xTaskCreate(ui_task, "ui", 6144, nullptr, 1, nullptr);
    vTaskPrioritySet(nullptr, 2);   // logic outranks rendering
}

void loop() {
    static uint32_t last_ble_state;
    link_poll();
    ble_poll();
    clock_update();

    touch_event_t e = touch_poll();
    if (e.type != EV_NONE) {
        touch_stats.events++;
        char s[48];
        snprintf(s, sizeof s, "LOG ev=%d at %d,%d", e.type, e.x, e.y);
        link_send_line(s);
        on_touch(e);
    }

    if (app.clip_state == CLIP_COPYING && (int32_t)(now_ms() - copy_deadline) >= 0) {
        app.clip_state = CLIP_IDLE;
        app_message("No reply from PC");
    }

    uint32_t ble_state = ble_connected() | (ble_ready() << 1);
    if (ble_state != last_ble_state) { last_ble_state = ble_state; app_redraw(); }

    typer_step();
    jiggler_step();
    vTaskDelay(1);   // yield so the render task gets the CPU
}
