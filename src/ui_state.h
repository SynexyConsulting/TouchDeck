// Everything a page needs to draw itself. The firmware fills it from its live
// state once per frame (ui_state_fill); the PC app fills it from the board's
// STATE/TEXT/CLIPTEXT lines and draws the same pages with the same code (the
// device mirror, hostui/). Identical in src/ and esp32c3/src/.
//
// Fixed-size fields only (int32/uint32/float/char arrays, no padding): C#,
// Swift and Python share this layout. Append new fields at the end.
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

#define UI_CLIP_VIEW 1024           // clip bytes kept for drawing (the box shows far fewer)

enum { CLIP_IDLE, CLIP_COPYING, CLIP_PASTING };
enum { JIG_MOVING, JIG_STOP, JIG_CLICK_DOWN, JIG_MENU_OPEN, JIG_ESC_DOWN, JIG_RESUME };

#define JIG_SCALE_COUNT 3
static const float JIG_SCALES[JIG_SCALE_COUNT] = {1.0f, 1.5f, 2.0f};

typedef struct {
    int32_t screen;         // page index (each board's own order)
    int32_t sub;            // ESP32-C3: Settings > Bluetooth sub-page open
    int32_t time_s;         // RP2040 watch: seconds since midnight
    int32_t helper;         // the PC app is talking to the board
    int32_t link_ok;        // chip dot: RP2040 USB mounted, ESP32-C3 output ready
    int32_t muted;          // RP2040: watch tick muted
    int32_t timer_s;        // RP2040: stopwatch
    int32_t bt_mode;        // ESP32-C3: output mode is Bluetooth (the accent colour)
    int32_t bt_avail;       // ESP32-C3: a bond exists, Bluetooth is selectable
    int32_t bt_state;       // ESP32-C3: bt_state_t
    int32_t bt_ready;       // ESP32-C3: connected and subscribed
    int32_t bt_secs_left;   // ESP32-C3: pairing time left
    uint32_t bt_passkey;    // ESP32-C3: pairing PIN
    int32_t clip_len;       // full clip length
    int32_t clip_state;     // CLIP_*
    int32_t paste_pos;
    int32_t jig_on;
    int32_t jig_demo;       // ANIM 1: dot animates without HID
    int32_t jig_paused;     // held while a paste types
    int32_t jig_phase;      // JIG_*
    int32_t jig_letter;     // index into JIG_PATHS
    int32_t jig_scale;      // index into JIG_SCALES
    float jig_x, jig_y;     // dot, letter-box units (0..1000)
    int32_t jig_next_s;     // seconds to the next right-click menu
    int32_t jig_up_s;       // seconds since the jiggler started
    uint32_t jig_menus;     // menus opened
    char clip_src[12];      // where the clip came from
    char msg[40];           // transient status message, "" when none
    char bt_host[32];       // ESP32-C3: bonded PC's name
    char down_reason[32];   // ESP32-C3: why output isn't ready
    char clip[UI_CLIP_VIEW];   // the clip's first bytes (clip_len may be longer)
    int32_t jig_menu_on;    // Jiggler settings page (firmware 1.8.0): context menu on
    int32_t jig_key;        // 0 = ESC, 1 = F15
    int32_t jig_open_s;     // seconds the context menu stays open
    int32_t jig_pause_s;    // seconds to wait before the next letter
} ui_state_t;

#ifdef __cplusplus
}
#endif
