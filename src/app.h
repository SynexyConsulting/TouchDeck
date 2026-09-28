// State shared between core0 (USB, touch, typing, jiggler) and core1 (rendering).
#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "pico/mutex.h"

#define CLIP_MAX 8192

enum { SCR_WATCH, SCR_CLIP, SCR_JIG, SCR_COUNT };
enum { CLIP_IDLE, CLIP_COPYING, CLIP_PASTING };
enum { JIG_CIRCLE, JIG_STOP, JIG_CLICK_DOWN, JIG_MENU_OPEN, JIG_ESC_DOWN, JIG_RESUME };

#define JIG_SCALE_COUNT 3
static const float JIG_SCALES[JIG_SCALE_COUNT] = {1.0f, 1.5f, 2.0f};

typedef struct {
    volatile int screen;
    volatile uint32_t redraw_seq;   // bump to make core1 redraw
    volatile int time_s;            // seconds since midnight
    volatile bool tick_pending;     // core1 -> core0: watch frame shown, play tick
    volatile bool helper;           // PC helper is talking to us
    volatile bool usb_mounted;      // host has enumerated us (keyboard/mouse usable)
    volatile bool muted;            // silences the tick and touch clicks
    volatile uint32_t frames;       // core1 frame counter (diagnostics)
    volatile uint32_t loops;        // core0 main-loop counter (diagnostics)

    // Clipboard. clip/clip_src are guarded by clip_mtx.
    char clip[CLIP_MAX + 1];
    volatile int clip_len;
    char clip_src[12];
    volatile int clip_state;
    volatile int paste_pos;
    char msg[40];                   // transient status line, guarded by clip_mtx
    volatile uint32_t msg_until_ms;

    // Jiggler
    volatile bool jig_on;
    volatile bool jig_paused;       // held while a paste is typing
    volatile int jig_phase;
    volatile float jig_angle, jig_radius;
    volatile uint32_t jig_next_menu_ms;
    volatile uint32_t jig_menus;
    volatile uint32_t jig_started_ms;
    volatile int jig_scale_idx;     // 0..2 -> JIG_SCALES[] (BOOT button on the Jiggler page)

    // Watch stopwatch (BOOT button on the watch page). timer_s is what the face shows.
    volatile bool timer_running;
    volatile int timer_s;
} app_t;

extern app_t app;
extern mutex_t clip_mtx;

static inline void app_redraw(void) { app.redraw_seq++; }
void app_message(const char *text);
uint32_t now_ms(void);
