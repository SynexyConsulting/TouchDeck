// State shared between the logic task (Arduino loop: serial, touch, typing,
// jiggler) and the render task (ui.cpp). Same shape as the RP2040 version.
#pragma once
#include <Arduino.h>
#include "freertos/semphr.h"

#define CLIP_MAX 8192

enum { SCR_CLIP, SCR_JIG, SCR_SETTINGS, SCR_COUNT };
enum { CLIP_IDLE, CLIP_COPYING, CLIP_PASTING };
enum { JIG_CIRCLE, JIG_STOP, JIG_CLICK_DOWN, JIG_MENU_OPEN, JIG_ESC_DOWN, JIG_RESUME };

struct app_t {
    volatile int screen;
    volatile bool in_bt;            // Settings drilled into the Bluetooth page
    volatile uint32_t redraw_seq;   // bump to make the render task redraw
    volatile bool helper;           // PC helper is talking to us over USB
    volatile uint8_t pc_leds;       // helper's lock-key state (LEDS)
    volatile int time_s = -1;       // seconds since midnight from the helper, -1 = unknown
    volatile uint32_t frames;

    // Clipboard. clip/clip_src/msg are guarded by clip_mtx.
    char clip[CLIP_MAX + 1];
    volatile int clip_len;
    char clip_src[12] = "-";
    volatile int clip_state;
    volatile int paste_pos;
    char msg[40];
    volatile uint32_t msg_until_ms;

    // Jiggler
    volatile bool jig_on;
    volatile bool jig_paused;       // held while a paste is typing
    volatile int jig_phase;
    volatile float jig_angle, jig_radius;
    volatile uint32_t jig_next_menu_ms;
    volatile uint32_t jig_menus;
    volatile uint32_t jig_started_ms;
};

extern app_t app;
extern SemaphoreHandle_t clip_mtx;

inline void app_redraw() { app.redraw_seq++; }
void app_message(const char *text);
inline uint32_t now_ms() { return millis(); }
