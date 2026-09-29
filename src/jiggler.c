// Moves the pointer along an outlined letter (jig_paths.h, walked by the shared
// jig_motion engine). Every so often it stops, right-clicks to open a context
// menu, presses ESC to close it, then glides on to a new random letter.
#include <math.h>
#include "pico/stdlib.h"
#include "pico/rand.h"
#include "tusb.h"
#include "app.h"
#include "jig_motion.h"
#include "jiggler.h"
#include "usb_io.h"

#define STEP_MS        10
#define MENU_MIN_S     45     // seconds between context-menu events
#define MENU_MAX_S     150

static jig_motion_t m;
static float sent_x, sent_y;   // mouse offset from the start point we have told the host so far
static float scale = 1.f;      // eases toward JIG_SCALES[app.jig_scale_idx] so a change never jumps
static uint32_t next_ms;

static uint32_t rand_between(uint32_t lo, uint32_t hi) {
    return lo + get_rand_32() % (hi - lo + 1);
}

static void schedule_menu(void) {
    app.jig_next_menu_ms = now_ms() + 1000 * rand_between(MENU_MIN_S, MENU_MAX_S);
}

static void publish(void) {
    app.jig_letter = m.letter;
    app.jig_x = m.x;
    app.jig_y = m.y;
}

void jiggler_toggle(void) {
    jiggler_set(!app.jig_on);
}

void jiggler_set(bool on) {
    if (on == app.jig_on) return;
    app.jig_on = on;
    if (app.jig_on) {
        scale = JIG_SCALES[app.jig_scale_idx];
        jm_begin(&m, jm_pick(-1, get_rand_32()));
        sent_x = sent_y = 0.f;       // the mouse's current spot is the letter's start point
        publish();
        app.jig_phase = JIG_MOVING;
        app.jig_menus = 0;
        app.jig_started_ms = now_ms();
        next_ms = now_ms();
        schedule_menu();
    } else if (app.jig_phase == JIG_CLICK_DOWN || app.jig_phase == JIG_ESC_DOWN) {
        // Don't leave a button or key held.
        usb_mouse(0, 0, 0);
        usb_key(0, 0);
    }
    app_redraw();
}

void jiggler_demo_begin(void) {
    jm_begin(&m, jm_pick(-1, get_rand_32()));
    publish();
    next_ms = now_ms();
    app_redraw();
}

bool jiggler_idle(void) {
    return !app.jig_on || app.jig_phase == JIG_MOVING;
}

static void set_phase(int phase, uint32_t wait_lo, uint32_t wait_hi) {
    app.jig_phase = phase;
    next_ms = now_ms() + rand_between(wait_lo, wait_hi);
}

// Demo (ANIM 1, perf tests): walk the letter on screen without sending HID.
static void demo_step(void) {
    if (now_ms() < next_ms) return;
    next_ms = now_ms() + STEP_MS;
    jm_step(&m, STEP_MS / 1000.f);
    publish();
}

static void move_step(void) {
    jm_step(&m, STEP_MS / 1000.f);
    scale += (JIG_SCALES[app.jig_scale_idx] - scale) * 0.03f;   // ~95% settled after 1 s of 10 ms steps
    float tx, ty;
    jm_mouse(&m, scale, &tx, &ty);
    int dx = (int)lroundf(tx - sent_x);
    int dy = (int)lroundf(ty - sent_y);
    if (dx > 127) dx = 127; else if (dx < -127) dx = -127;
    if (dy > 127) dy = 127; else if (dy < -127) dy = -127;
    publish();
    if ((dx || dy) && !usb_mouse(0, (int8_t)dx, (int8_t)dy)) return;   // the next step catches up
    sent_x += dx;
    sent_y += dy;
}

void jiggler_step(void) {
    if (!app.jig_on) {
        if (app.anim_demo) demo_step();
        return;
    }
    if (app.jig_paused) return;
    if (!tud_mounted() || now_ms() < next_ms || !usb_hid_ready()) return;

    switch (app.jig_phase) {
    case JIG_MOVING:
        next_ms = now_ms() + STEP_MS;
        if ((int32_t)(now_ms() - app.jig_next_menu_ms) >= 0) set_phase(JIG_STOP, 400, 900);
        else move_step();
        break;
    case JIG_STOP:            // pointer has settled: open the context menu
        if (usb_mouse(MOUSE_BUTTON_RIGHT, 0, 0)) set_phase(JIG_CLICK_DOWN, 60, 120);
        break;
    case JIG_CLICK_DOWN:
        if (usb_mouse(0, 0, 0)) set_phase(JIG_MENU_OPEN, 800, 2000);
        break;
    case JIG_MENU_OPEN:       // menu has been visible a moment: close it
        if (usb_key(0, HID_KEY_ESCAPE)) set_phase(JIG_ESC_DOWN, 50, 90);
        break;
    case JIG_ESC_DOWN:
        if (usb_key(0, 0)) set_phase(JIG_RESUME, 300, 700);
        break;
    case JIG_RESUME:
        app.jig_menus++;
        schedule_menu();
        jm_switch(&m, jm_pick(m.letter, get_rand_32()));   // a new letter after every menu
        publish();
        app_redraw();                                     // draw the new letter
        app.jig_phase = JIG_MOVING;
        next_ms = now_ms();
        break;
    }
}
