// State shared between core0 (USB, touch, typing, jiggler) and core1 (rendering).
#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "pico/mutex.h"

#define CLIP_MAX 8192

enum { SCR_WATCH, SCR_CLIP, SCR_JIG, SCR_COUNT };
enum { CLIP_IDLE, CLIP_COPYING, CLIP_PASTING };
enum { JIG_MOVING, JIG_STOP, JIG_CLICK_DOWN, JIG_MENU_OPEN, JIG_ESC_DOWN, JIG_RESUME };

#define JIG_SCALE_COUNT 3
static const float JIG_SCALES[JIG_SCALE_COUNT] = {1.0f, 1.5f, 2.0f};

typedef struct {
    volatile int screen;
    volatile uint32_t redraw_seq;   // bump to make core1 redraw
    volatile int time_s;            // seconds since midnight
    volatile uint32_t second_edge;  // bumped by the second-alarm IRQ (core0) on every clock second
    volatile uint32_t edge_us;      // time_us_32() of the latest second edge
    volatile bool helper;           // PC helper is talking to us
    volatile bool usb_mounted;      // host has enumerated us (keyboard/mouse usable)
    volatile bool muted;            // silences the once-a-second watch tick
    volatile uint32_t frames;       // core1 frame counter (diagnostics)
    volatile uint32_t loops;        // core0 main-loop counter (diagnostics)
    volatile uint32_t perf_draw_us, perf_push_us, perf_draw_max_us;   // frame timing (DBG)
    volatile uint32_t perf_edge_lag_us, prerender_hits, prerender_misses;   // watch second timing (DBG)
    volatile uint32_t watch_tick;   // core0 -> core1: the watch's second/stopwatch changed (partial redraw)
    volatile bool anim_demo;        // ANIM 1: animate the jiggler page without sending HID (perf tests)

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
    volatile int jig_letter;        // index into JIG_PATHS (jig_paths.h)
    volatile float jig_x, jig_y;    // dot position in letter-box units (0..1000)
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
void clip_clear(void);          // empty the clip (ignored when empty or pasting)
void jig_cycle_scale(void);     // jiggler scale 1x -> 1.5x -> 2x -> 1x, saved
uint32_t now_ms(void);
