#pragma once

void jiggler_set(bool on);
void jiggler_toggle();
bool jiggler_idle();   // off, or moving along the letter (safe to start typing)
int jiggler_next_menu_s();   // DBG: countdown to the next menu (paused value when off)
void jiggler_demo_begin();   // ANIM 1: walk a letter on screen, no HID
void jiggler_step();   // call from the logic loop
void jiggler_on_output_change();   // output mode switched
