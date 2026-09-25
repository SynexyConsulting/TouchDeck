// Output routing: every keystroke / mouse report goes through here, and the
// output mode decides where it lands (spec: docs/superpowers/specs/2026-09-24-output-mode-design.md).
//   PC: "K"/"M" lines over USB serial; tools/clip_helper.py performs them.
//   BT: Bluetooth LE HID reports to the bonded host.
#pragma once
#include <stdint.h>

enum out_mode_t { MODE_PC = 0, MODE_BT = 1 };

void mode_init();                   // load the saved mode; call after ble_init()
out_mode_t mode_get();              // effective mode: BT only while bonded
bool mode_bt_available();           // a Bluetooth bond exists
bool mode_set(out_mode_t m);        // false (no change) if BT isn't available

bool out_ready();                   // the current mode's link is live
const char *out_down_reason();      // why out_ready() is false, for the UI
bool out_key(uint8_t mod, uint8_t usage);            // usage 0 = release all
bool out_mouse(uint8_t buttons, int8_t dx, int8_t dy);
bool out_caps_lock();
uint32_t out_key_pace_ms();         // key hold and gap for the current sink
