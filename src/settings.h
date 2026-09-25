#pragma once
#include <stdbool.h>

// Persistent settings in the last 4 KB sector of flash.
void settings_load(void);    // fills app.* from flash (defaults if none saved)
void settings_save(void);    // writes app.* if changed; call from core0 only
bool settings_jig_on(void);  // saved jiggler state, to restart it at boot
