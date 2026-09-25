#pragma once
#include "touch.h"

// Core1 entry point: renders whichever screen is active and pushes frames.
void ui_core1_main(void);

// Button hit areas, shared by drawing (core1) and touch handling (core0).
#define BTN_Y      204
#define BTN_H      44
#define BTN_COPY_X 20
#define BTN_PASTE_X 122
#define BTN_W      98

// Mute toggle in the watch face's top-right corner, clear of the dial.
#define MUTE_CX 211
#define MUTE_CY 30
#define MUTE_HIT_X 186   // tap target: x >= MUTE_HIT_X, y < MUTE_HIT_Y
#define MUTE_HIT_Y 58

#define JIG_CX 120
#define JIG_CY 140
#define JIG_R  62
