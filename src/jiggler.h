#pragma once
#include <stdbool.h>

void jiggler_set(bool on);
void jiggler_toggle(void);
bool jiggler_idle(void);   // off, or circling (safe to start typing)
void jiggler_step(void);   // call from the main loop
