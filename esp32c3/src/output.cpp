#include <Arduino.h>
#include <Preferences.h>
#include "app.h"
#include "ble_hid.h"
#include "jiggler.h"
#include "link.h"
#include "output.h"
#include "typer.h"

static Preferences prefs;
static out_mode_t wanted = MODE_PC;   // the user's choice (saved)
static bool key_held[2];              // a non-release key report is outstanding, per sink
static uint8_t buttons_held[2];

void mode_init() {
    prefs.begin("touchdeck", false);
    wanted = prefs.getUChar("mode", MODE_PC) == MODE_BT ? MODE_BT : MODE_PC;
    if (wanted == MODE_BT && !ble_bonded()) {   // bond gone since last run
        wanted = MODE_PC;
        prefs.putUChar("mode", MODE_PC);
    }
}

bool mode_bt_available() { return ble_bonded(); }

out_mode_t mode_get() { return (wanted == MODE_BT && ble_bonded()) ? MODE_BT : MODE_PC; }

static bool sink_key(out_mode_t m, uint8_t mod, uint8_t usage) {
    bool ok;
    if (m == MODE_BT) {
        ok = ble_key(mod, usage);
    } else {
        if (!app.helper) return false;
        char s[16];
        snprintf(s, sizeof s, "K %02X %02X", usage ? mod : 0, usage);
        link_send_line(s);
        ok = true;
    }
    if (ok) key_held[m] = usage != 0;
    return ok;
}

static bool sink_mouse(out_mode_t m, uint8_t buttons, int8_t dx, int8_t dy) {
    bool ok;
    if (m == MODE_BT) {
        ok = ble_mouse(buttons, dx, dy);
    } else {
        if (!app.helper) return false;
        char s[24];
        snprintf(s, sizeof s, "M %02X %d %d", buttons, dx, dy);
        link_send_line(s);
        ok = true;
    }
    if (ok) buttons_held[m] = buttons;
    return ok;
}

static void release_all(out_mode_t m) {
    if (key_held[m]) sink_key(m, 0, 0);
    if (buttons_held[m]) sink_mouse(m, 0, 0, 0);
}

bool mode_set(out_mode_t m) {
    if (m == MODE_BT && !ble_bonded()) return false;
    out_mode_t old = mode_get();
    wanted = m;
    prefs.putUChar("mode", m);
    if (mode_get() != old) {
        if (typer_busy()) typer_cancel();
        release_all(old);                 // nothing stays held on the old destination
        jiggler_on_output_change();
    }
    app_redraw();
    return true;
}

bool out_ready() { return mode_get() == MODE_BT ? ble_ready() : app.helper; }

const char *out_down_reason() {
    return mode_get() == MODE_BT ? "Waiting for Bluetooth" : "Start the PC helper";
}

bool out_key(uint8_t mod, uint8_t usage) { return sink_key(mode_get(), mod, usage); }
bool out_mouse(uint8_t buttons, int8_t dx, int8_t dy) { return sink_mouse(mode_get(), buttons, dx, dy); }
bool out_caps_lock() { return mode_get() == MODE_BT ? ble_caps_lock() : (app.pc_leds & 0x02); }
uint32_t out_key_pace_ms() { return mode_get() == MODE_BT ? 12 : 4; }
