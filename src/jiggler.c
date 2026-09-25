// Moves the pointer in a loose, wobbling circle. Every so often it stops,
// right-clicks to open a context menu, presses ESC to close it, and carries on.
#include <math.h>
#include "pico/stdlib.h"
#include "pico/rand.h"
#include "tusb.h"
#include "app.h"
#include "jiggler.h"
#include "usb_io.h"

#define STEP_MS        10
#define MENU_MIN_S     45     // seconds between context-menu events
#define MENU_MAX_S     150

static float t;                // circle "time", only advances while circling
static float angle;
static float sent_x, sent_y;   // position we have told the host so far
static uint32_t next_ms;

static uint32_t rand_between(uint32_t lo, uint32_t hi) {
    return lo + get_rand_32() % (hi - lo + 1);
}

static void schedule_menu(void) {
    app.jig_next_menu_ms = now_ms() + 1000 * rand_between(MENU_MIN_S, MENU_MAX_S);
}

static float radius_at(float tt) {
    return 60.f + 22.f * sinf(tt * 0.23f) + 9.f * sinf(tt * 0.71f + 1.f);
}

void jiggler_toggle(void) {
    jiggler_set(!app.jig_on);
}

void jiggler_set(bool on) {
    if (on == app.jig_on) return;
    app.jig_on = on;
    if (app.jig_on) {
        t = 0.f;
        angle = 0.f;
        float r = radius_at(t);
        sent_x = r;       // start "on" the circle so the first step isn't a jump
        sent_y = 0.f;
        app.jig_phase = JIG_CIRCLE;
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

bool jiggler_idle(void) {
    return !app.jig_on || app.jig_phase == JIG_CIRCLE;
}

static void set_phase(int phase, uint32_t wait_lo, uint32_t wait_hi) {
    app.jig_phase = phase;
    next_ms = now_ms() + rand_between(wait_lo, wait_hi);
}

static void circle_step(void) {
    float dt = STEP_MS / 1000.f;
    t += dt;
    // ~4 s per lap, with speed drifting +/-30% so it never looks mechanical.
    float omega = 6.2832f / 4.f * (1.f + 0.3f * sinf(t * 0.37f));
    angle += omega * dt;
    if (angle > 6.2832f) angle -= 6.2832f;
    float r = radius_at(t);
    float tx = r * cosf(angle), ty = r * sinf(angle);

    int dx = (int)lroundf(tx - sent_x);
    int dy = (int)lroundf(ty - sent_y);
    if (dx > 127) dx = 127; else if (dx < -127) dx = -127;
    if (dy > 127) dy = 127; else if (dy < -127) dy = -127;
    if ((dx || dy) && !usb_mouse(0, (int8_t)dx, (int8_t)dy)) return;
    sent_x += dx;
    sent_y += dy;
    app.jig_angle = angle;
    app.jig_radius = r;
}

void jiggler_step(void) {
    if (!app.jig_on || app.jig_paused) return;
    if (!tud_mounted() || now_ms() < next_ms || !usb_hid_ready()) return;

    switch (app.jig_phase) {
    case JIG_CIRCLE:
        next_ms = now_ms() + STEP_MS;
        if ((int32_t)(now_ms() - app.jig_next_menu_ms) >= 0) set_phase(JIG_STOP, 400, 900);
        else circle_step();
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
        app.jig_phase = JIG_CIRCLE;
        next_ms = now_ms();
        break;
    }
}
