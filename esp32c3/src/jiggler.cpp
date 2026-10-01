// Moves the pointer along an outlined letter (jig_paths.h, walked by the shared
// jig_motion engine). Every so often it stops, right-clicks to open a context
// menu, presses ESC to close it, then glides on to a new random letter.
// Identical behaviour to the RP2040 version, sent through the output layer.
#include <math.h>
#include "esp_random.h"
#include "app.h"
#include "jig_menu.h"
#include "jig_motion.h"
#include "output.h"
#include "jiggler.h"

#define STEP_MS        15     // one BLE connection interval-ish per report
#define MENU_MIN_S     45     // seconds between context-menu events
#define MENU_MAX_S     150

static jig_motion_t m;
static float sent_x, sent_y;   // mouse offset from the start point we have told the host so far
static float scale = 1.f;      // eases toward JIG_SCALES[app.jig_scale_idx] so a change never jumps
static uint32_t next_ms;
static uint32_t paused_menu_ms;
static bool release_mouse, release_key;   // owed to the host after an interrupted menu   // countdown left when switched off while moving (0 = none)

static uint32_t rand_between(uint32_t lo, uint32_t hi) {
    return lo + esp_random() % (hi - lo + 1);
}

static void schedule_menu() {
    app.jig_next_menu_ms = now_ms() + 1000 * rand_between(MENU_MIN_S, MENU_MAX_S);
}

static void publish() {
    app.jig_letter = m.letter;
    app.jig_x = m.x;
    app.jig_y = m.y;
}

void jiggler_toggle() { jiggler_set(!app.jig_on); }

void jiggler_set(bool on) {
    if (on == app.jig_on) return;
    app.jig_on = on;
    if (on) {
        scale = JIG_SCALES[app.jig_scale_idx];
        jm_begin(&m, app.jig_letter);   // keep the letter on screen; it changes after each menu
        sent_x = sent_y = 0.f;   // the mouse's current spot is the letter's start point
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
        // Don't leave a button or key held; jiggler_step retries each release
        // until the output accepts it (a BLE notify can fail).
        release_mouse = release_key = true;
    }
    app_redraw();
}

void jiggler_demo_begin() {
    jm_begin(&m, jm_pick(-1, esp_random()));
    publish();
    next_ms = now_ms();
    app_redraw();
}

// The old destination's right-click/Esc sequence is abandoned (output.cpp
// already released anything held there); start a fresh cycle.
void jiggler_on_output_change() {
    if (!app.jig_on) return;
    app.jig_phase = JIG_MOVING;
    schedule_menu();
    next_ms = now_ms();
}

int jiggler_next_menu_s() {
    if (!app.jig_on) return (int)(paused_menu_ms / 1000);
    int32_t left = (int32_t)(app.jig_next_menu_ms - now_ms());
    return left > 0 ? left / 1000 : 0;
}

void jiggler_menu_now() {
    if (app.jig_on && app.jig_phase == JIG_MOVING) app.jig_next_menu_ms = now_ms();
}

bool jiggler_idle() { return !app.jig_on || app.jig_phase == JIG_MOVING; }

// Demo (ANIM 1, perf tests): walk the letter on screen without sending HID.
static void demo_step() {
    if ((int32_t)(now_ms() - next_ms) < 0) return;
    next_ms = now_ms() + STEP_MS;
    jm_step(&m, STEP_MS / 1000.f);
    publish();
}

static void move_step() {
    jm_step(&m, STEP_MS / 1000.f);
    scale += (JIG_SCALES[app.jig_scale_idx] - scale) * 0.045f;   // ~95% settled after 1 s of 15 ms steps
    float tx, ty;
    jm_mouse(&m, scale, &tx, &ty);
    int dx = (int)lroundf(tx - sent_x);
    int dy = (int)lroundf(ty - sent_y);
    dx = dx > 127 ? 127 : (dx < -127 ? -127 : dx);
    dy = dy > 127 ? 127 : (dy < -127 ? -127 : dy);
    publish();
    if ((dx || dy) && !out_mouse(0, (int8_t)dx, (int8_t)dy)) return;   // the next step catches up
    sent_x += dx;
    sent_y += dy;
}

void jiggler_step() {
    if (release_mouse || release_key) {
        if (!out_ready()) return;
        if (release_mouse) { if (out_mouse(0, 0, 0)) release_mouse = false; return; }
        if (out_key(0, 0)) release_key = false;
        return;
    }
    if (!app.jig_on) {
        if (app.anim_demo) demo_step();
        return;
    }
    if (app.jig_paused) return;
    if (!out_ready() || (int32_t)(now_ms() - next_ms) < 0) return;

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
    jm_step_t st = jmenu_step(app.jig_phase, &app.jig_cfg, esp_random());
    switch (st.action) {
    case JM_RIGHT_DOWN: if (!out_mouse(0x02, 0, 0)) return; break;   // right button
    case JM_RIGHT_UP:   if (!out_mouse(0, 0, 0)) return; break;
    case JM_KEY_DOWN:   if (!out_key(0, jmenu_key(&app.jig_cfg))) return; break;
    case JM_KEY_UP:     if (!out_key(0, 0)) return; break;
    case JM_SWITCH:
        app.jig_menus++;
        schedule_menu();
        jm_switch(&m, jm_pick(m.letter, esp_random()));   // a new letter after every menu
        publish();
        app_redraw();                                     // draw the new letter
        break;
    default: break;
    }
    app.jig_phase = st.next_phase;
    next_ms = now_ms() + st.wait_ms;
}
