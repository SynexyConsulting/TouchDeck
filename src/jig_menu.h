// The jiggler's periodic "menu event", as a pure state machine shared by every
// board (identical in src/ and esp32c3/src/, host-tested by test_jig_menu.py).
// It decides what to send and how long to wait; jiggler.c / jiggler.cpp send it
// (USB HID, Bluetooth or the PC app) and advance only once the send went out.
//
//   stop -> [right-click, hold "open"] -> key (ESC or F15) -> wait "pause" -> new letter
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

// The settings on the "Jiggler settings" page (saved on the board).
typedef struct {
    uint8_t menu_on;   // 1: right-click and hold the context menu before the key
    uint8_t key_f15;   // 0: ESC (closes the menu), 1: F15 (a key no app acts on)
    uint8_t open_s;    // seconds the context menu stays open, 0-60
    uint8_t pause_s;   // seconds to wait after the key, before the next letter, 0-60
} jig_cfg_t;

#define JM_OPEN_DEFAULT  2
#define JM_MAX_S         60
#define JM_SETTLE_MS     300     // always between the key and the next letter
#define JM_KEY_ESC       0x29    // HID usage
#define JM_KEY_F15       0x6A

enum { JM_NONE, JM_RIGHT_DOWN, JM_RIGHT_UP, JM_KEY_DOWN, JM_KEY_UP, JM_SWITCH };

typedef struct {
    uint8_t action;      // JM_*: what to send now
    uint8_t next_phase;  // JIG_* (ui_state.h) once it went out
    uint32_t wait_ms;    // then wait this long before the next step
} jm_step_t;

// Defaults: menu on, ESC, open 2 s, pause 0 s.
jig_cfg_t jmenu_defaults(void);
// Flags to 0/1, seconds to 0..60.
void jmenu_clamp(jig_cfg_t *c);
// The key this configuration presses (HID usage).
uint8_t jmenu_key(const jig_cfg_t *c);
// One step of the event from `phase` (JIG_STOP .. JIG_RESUME). `rnd` jitters the
// short press lengths only; the configured times are exact.
jm_step_t jmenu_step(int phase, const jig_cfg_t *c, uint32_t rnd);

#ifdef __cplusplus
}
#endif
