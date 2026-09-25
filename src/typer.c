#include "pico/stdlib.h"
#include "tusb.h"
#include "app.h"
#include "typer.h"
#include "usb_io.h"
#include "jiggler.h"

// Some apps drop keys that arrive back-to-back, so hold each key briefly.
#define HOLD_US 3000
#define GAP_US  3000

static const uint8_t ascii_map[128][2] = {HID_ASCII_TO_KEYCODE};   // {shift, keycode}

static bool pending, active, key_down;
static int pos;
static uint64_t next_us;

void typer_start(void) {
    if (active || pending || app.clip_len == 0) return;
    pending = true;
    app.clip_state = CLIP_PASTING;
    app.paste_pos = 0;
}

static void finish(void) {
    pending = active = false;
    app.clip_state = CLIP_IDLE;
    app.jig_paused = false;
}

void typer_cancel(void) {
    if (key_down) usb_key(0, 0);   // never leave a key stuck down
    key_down = false;
    finish();
    app_message("Paste stopped");
}

bool typer_busy(void) { return pending || active; }

// Next typeable character at pos, or -1 at the end. CRLF and lone CR become Enter.
static int next_char(void) {
    while (pos < app.clip_len) {
        unsigned char c = (unsigned char)app.clip[pos++];
        if (c == '\r') {
            if (pos < app.clip_len && app.clip[pos] == '\n') continue;
            c = '\n';
        }
        if (c < 128 && ascii_map[c][1]) return c;
        // Non-ASCII / unmappable: skip it.
    }
    return -1;
}

void typer_step(void) {
    if (pending) {
        // Let the jiggler finish any right-click/ESC sequence, then hold it.
        if (!jiggler_idle()) return;
        app.jig_paused = true;
        pending = false;
        active = true;
        key_down = false;
        pos = 0;
        next_us = time_us_64();
    }
    if (!active) return;
    if (!tud_mounted()) { finish(); return; }
    if (time_us_64() < next_us || !usb_hid_ready()) return;

    if (key_down) {
        usb_key(0, 0);
        key_down = false;
        next_us = time_us_64() + GAP_US;
        app.paste_pos = pos;
        if (pos >= app.clip_len) {
            finish();
            app_message("Pasted");
        }
        return;
    }

    int c = next_char();
    if (c < 0) {
        finish();
        app_message("Pasted");
        return;
    }
    uint8_t shift = ascii_map[c][0];
    // Caps Lock on the host flips letter case, so compensate.
    if (usb_caps_lock() && ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) shift = !shift;
    usb_key(shift ? KEYBOARD_MODIFIER_LEFTSHIFT : 0, ascii_map[c][1]);
    key_down = true;
    next_us = time_us_64() + HOLD_US;
}
