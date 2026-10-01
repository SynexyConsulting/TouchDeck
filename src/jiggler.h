#pragma once
#include <stdbool.h>

void jiggler_set(bool on);
void jiggler_toggle(void);
bool jiggler_idle(void);   // off, moving along the letter, or in the post-key pause (safe to start typing)
int jiggler_next_menu_s(void);   // DBG: countdown to the next menu (paused value when off)
void jiggler_demo_begin(void);
void jiggler_menu_now(void);   // JIG MENU (tests): start the right-click/Esc sequence now   // ANIM 1: walk a letter on screen, no HID
void jiggler_step(void);   // call from the main loop
