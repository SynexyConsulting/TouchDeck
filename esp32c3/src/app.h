// State shared between the logic task (Arduino loop: serial, touch, typing,
// jiggler) and the render task (ui.cpp). Same shape as the RP2040 version.
#pragma once
#include <Arduino.h>
#include "freertos/semphr.h"
#include "jig_menu.h"
#include "ui_pages.h"      // SCR_*, and via ui_state.h CLIP_*, JIG_* phases, JIG_SCALES

#define CLIP_MAX 8192

struct app_t {
    volatile int screen;
    volatile bool in_bt;            // Settings drilled into the Bluetooth page
    volatile uint32_t redraw_seq;   // bump to make the render task redraw
    volatile bool helper;           // PC helper is talking to us over USB
    volatile uint8_t pc_leds;       // helper's lock-key state (LEDS)
    volatile int time_s = -1;       // seconds since midnight from the helper, -1 = unknown
    volatile int timer_s;           // the watch's stopwatch (BOOT on the watch page)
    volatile bool timer_running;
    volatile uint32_t frames;

    // Clipboard. clip/clip_src/msg are guarded by clip_mtx.
    char clip[CLIP_MAX + 1];
    volatile int clip_len;
    volatile uint32_t clip_seq;     // bumped whenever the clip's text changes (mirror sync)
    char clip_src[12] = "-";
    volatile int clip_state;
    volatile int paste_pos;
    char msg[40];
    volatile uint32_t msg_until_ms;

    // Jiggler
    volatile bool jig_on;
    volatile bool jig_paused;       // held while a paste is typing
    volatile int jig_phase;
    volatile int jig_letter;        // index into JIG_PATHS (jig_paths.h)
    volatile float jig_x, jig_y;    // dot position in letter-box units (0..1000)
    volatile int jig_scale_idx;     // 0..2 -> JIG_SCALES[] (scale pill), Preferences "jscale"
    jig_cfg_t jig_cfg;              // Jiggler settings page; Preferences jmenu, jkey, jopen, jpause
    volatile bool jig_settings;     // the Jiggler settings panel is open over the Jiggler page
    volatile bool anim_demo;        // ANIM 1: animate the jiggler page without sending HID
    volatile uint32_t jig_next_menu_ms;
    volatile uint32_t jig_menus;
    volatile uint32_t jig_started_ms;
};

extern app_t app;
extern SemaphoreHandle_t clip_mtx;

inline void app_redraw() { app.redraw_seq++; }
void app_message(const char *text);
void clip_clear();         // empty the clip (ignored when empty or pasting)
void jig_cycle_scale();    // jiggler scale 1x -> 1.5x -> 2x -> 1x, saved
void jig_set_cfg(const jig_cfg_t *c);   // Jiggler settings: clamp, apply, redraw, save
inline uint32_t now_ms() { return millis(); }
