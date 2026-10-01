// Moves the pointer along an outlined letter (jig_paths.h, walked by the shared
// jig_motion engine). Every so often it runs the menu event from the shared
// jig_menu engine (app.jig_cfg, the Jiggler settings page): by default it stops,
// right-clicks, holds the context menu 2 s, presses ESC, then glides on to a new
// random letter.
#include <math.h>
#include "pico/stdlib.h"
#include "pico/rand.h"
#include "tusb.h"
#include "app.h"
#include "jig_menu.h"
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
static uint32_t paused_menu_ms;
static bool release_mouse, release_key;   // owed to the host after an interrupted menu   // countdown left when switched off while moving (0 = none)

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
        jm_begin(&m, app.jig_letter);   // keep the letter on screen; it changes after each menu
        sent_x = sent_y = 0.f;       // the mouse's current spot is the letter's start point
        publish();
        app.jig_phase = JIG_MOVING;
        app.jig_menus = 0;
        app.jig_started_ms = now_ms();
        next_ms = now_ms();
        if (paused_menu_ms) app.jig_next_menu_ms = now_ms() + paused_menu_ms;   // carry on the countdown
        else schedule_menu();
        paused_menu_ms = 0;
    } else if (app.jig_phase == JIG_MOVING) {
        // Switched off mid-countdown: remember what was left for the next start.
        int32_t left = (int32_t)(app.jig_next_menu_ms - now_ms());
        paused_menu_ms = left > 0 ? (uint32_t)left : 0;
    } else if (app.jig_phase == JIG_CLICK_DOWN || app.jig_phase == JIG_ESC_DOWN) {
        // Don't leave a button or key held. Both reports share one HID endpoint,
        // so the second can't go out in the same instant: jiggler_step retries
        // each until the host has it.
        release_mouse = release_key = true;
    }
    app_redraw();
}

void jiggler_demo_begin(void) {
    jm_begin(&m, jm_pick(-1, get_rand_32()));
    publish();
    next_ms = now_ms();
    app_redraw();
}

int jiggler_next_menu_s(void) {
    if (!app.jig_on) return (int)(paused_menu_ms / 1000);
    int32_t left = (int32_t)(app.jig_next_menu_ms - now_ms());
    return left > 0 ? left / 1000 : 0;
}

void jiggler_menu_now(void) {
    if (app.jig_on && app.jig_phase == JIG_MOVING) app.jig_next_menu_ms = now_ms();
}

bool jiggler_idle(void) {
    return !app.jig_on || app.jig_phase == JIG_MOVING;
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
    if (release_mouse || release_key) {
        if (!tud_mounted() || !usb_hid_ready()) return;
        if (release_mouse) { if (usb_mouse(0, 0, 0)) release_mouse = false; return; }
        if (usb_key(0, 0)) release_key = false;
        return;
    }
    if (!app.jig_on) {
        if (app.anim_demo) demo_step();
        return;
    }
    if (app.jig_paused) return;
    if (!tud_mounted() || now_ms() < next_ms || !usb_hid_ready()) return;

    if (app.jig_phase == JIG_MOVING) {
        next_ms = now_ms() + STEP_MS;
        if ((int32_t)(now_ms() - app.jig_next_menu_ms) >= 0) {
            app.jig_phase = JIG_STOP;                     // let the pointer settle first
            next_ms = now_ms() + rand_between(400, 900);
        } else {
            move_step();
        }
        return;
    }

    // The menu event: the shared engine decides what to send; advance only once it went out.
    jm_step_t st = jmenu_step(app.jig_phase, &app.jig_cfg, get_rand_32());
    switch (st.action) {
    case JM_RIGHT_DOWN: if (!usb_mouse(MOUSE_BUTTON_RIGHT, 0, 0)) return; break;
    case JM_RIGHT_UP:   if (!usb_mouse(0, 0, 0)) return; break;
    case JM_KEY_DOWN:   if (!usb_key(0, jmenu_key(&app.jig_cfg))) return; break;
    case JM_KEY_UP:     if (!usb_key(0, 0)) return; break;
    case JM_SWITCH:
        app.jig_menus++;
        schedule_menu();
        jm_switch(&m, jm_pick(m.letter, get_rand_32()));   // a new letter after every menu
        publish();
        app_redraw();                                     // draw the new letter
        break;
    default: break;
    }
    app.jig_phase = st.next_phase;
    next_ms = now_ms() + st.wait_ms;
}
