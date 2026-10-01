// See jig_menu.h. Plain C, no SDK calls: shared by every board and the host tests.
#include "jig_menu.h"
#include "ui_state.h"   // JIG_* phases

static uint32_t between(uint32_t rnd, uint32_t lo, uint32_t hi) { return lo + rnd % (hi - lo + 1); }

jig_cfg_t jmenu_defaults(void) {
    jig_cfg_t c = {1, 0, JM_OPEN_DEFAULT, 0};
    return c;
}

void jmenu_clamp(jig_cfg_t *c) {
    c->menu_on = c->menu_on ? 1 : 0;
    c->key_f15 = c->key_f15 ? 1 : 0;
    if (c->open_s > JM_MAX_S) c->open_s = JM_MAX_S;
    if (c->pause_s > JM_MAX_S) c->pause_s = JM_MAX_S;
}

uint8_t jmenu_key(const jig_cfg_t *c) { return c->key_f15 ? JM_KEY_F15 : JM_KEY_ESC; }

jm_step_t jmenu_step(int phase, const jig_cfg_t *c, uint32_t rnd) {
    jm_step_t s = {JM_NONE, JIG_MOVING, 0};
    switch (phase) {
    case JIG_STOP:              // the pointer has settled
        if (c->menu_on) { s.action = JM_RIGHT_DOWN; s.next_phase = JIG_CLICK_DOWN; s.wait_ms = between(rnd, 60, 120); }
        else { s.action = JM_KEY_DOWN; s.next_phase = JIG_ESC_DOWN; s.wait_ms = between(rnd, 50, 90); }
        break;
    case JIG_CLICK_DOWN:        // button up: the context menu opens and stays for "open"
        s.action = JM_RIGHT_UP; s.next_phase = JIG_MENU_OPEN; s.wait_ms = (uint32_t)c->open_s * 1000u;
        break;
    case JIG_MENU_OPEN:         // the key (ESC closes the menu)
        s.action = JM_KEY_DOWN; s.next_phase = JIG_ESC_DOWN; s.wait_ms = between(rnd, 50, 90);
        break;
    case JIG_ESC_DOWN:          // key up, then "pause" (+ a short settle) before the next letter
        s.action = JM_KEY_UP; s.next_phase = JIG_RESUME; s.wait_ms = (uint32_t)c->pause_s * 1000u + JM_SETTLE_MS;
        break;
    case JIG_RESUME:            // new letter, back to moving
        s.action = JM_SWITCH; s.next_phase = JIG_MOVING;
        break;
    default:
        break;
    }
    return s;
}
