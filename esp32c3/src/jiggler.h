#pragma once

void jiggler_set(bool on);
void jiggler_toggle();
bool jiggler_idle();   // off, or circling (safe to start typing)
void jiggler_step();   // call from the logic loop
