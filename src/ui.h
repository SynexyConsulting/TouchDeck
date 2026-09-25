#pragma once
#include "touch.h"

// Core1 entry point: renders whichever screen is active and pushes frames.
void ui_core1_main(void);

// Button hit areas, shared by drawing (core1) and touch handling (core0).
#define BTN_Y      178
#define BTN_H      58
#define BTN_COPY_X 14
#define BTN_PASTE_X 124
#define BTN_W      102

// Mute toggle in the watch face's top-right corner, clear of the dial.
#define MUTE_CX 211
#define MUTE_CY 30
#define MUTE_HIT_X 186   // tap target: x >= MUTE_HIT_X, y < MUTE_HIT_Y
#define MUTE_HIT_Y 58

#define JIG_CX 120
#define JIG_CY 118
#define JIG_R  74
