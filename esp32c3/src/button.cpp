// BOOT on the ESP32-2424S012C is GPIO9 (a strapping pin, fine to read at
// runtime), pulled up, low while pressed.
#include <Arduino.h>
#include "board.h"
#include "button.h"
#include "app.h"

#define LONG_MS 800

static bool stable, last_raw, long_fired;
static uint32_t pressed_ms;

static bool read_raw() { return digitalRead(BOOT_BTN_PIN) == LOW; }

void button_init() {
    pinMode(BOOT_BTN_PIN, INPUT_PULLUP);
    stable = last_raw = read_raw();
}

btn_ev_t button_poll() {
    bool raw = read_raw();
    bool settled = raw == last_raw;        // two equal samples 20 ms apart = debounced
    last_raw = raw;
    if (!settled) return BTN_NONE;

    if (raw && !stable) {                  // press
        stable = true;
        long_fired = false;
        pressed_ms = now_ms();
    } else if (!raw && stable) {           // release
        stable = false;
        return long_fired ? BTN_NONE : BTN_SHORT;
    } else if (raw && !long_fired && now_ms() - pressed_ms >= LONG_MS) {
        long_fired = true;
        return BTN_LONG;
    }
    return BTN_NONE;
}
