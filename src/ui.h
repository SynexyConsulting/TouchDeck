#pragma once
#include "touch.h"

// Core1 entry point: renders whichever screen is active and pushes frames.
void ui_core1_main(void);

// Hit areas and layout, shared by drawing (core1) and touch handling (core0).
#define TITLE_Y    42          // page titles (centre of capitals)

// Clipboard: text box, then the buttons, then the status line.
#define BTN_Y      176
#define BTN_H      44
#define BTN_COPY_X 20
#define BTN_PASTE_X 122
#define BTN_W      98
#define CLIP_STATUS_Y 238
#define TRASH_CX   196
#define TRASH_CY   43
#define TRASH_HIT  18          // half-size of the square tap target

// Mute toggle in the watch face's top-right corner, inside the corner ticks.
#define MUTE_CX 196
#define MUTE_CY 54
#define MUTE_HIT_X 176   // tap target: x >= MUTE_HIT_X, y < MUTE_HIT_Y
#define MUTE_HIT_Y 76

// Jiggler: pills either side of the title; the letter lane below (jig_paths.h).
#define PILL_Y     33
#define PILL_H     18
#define PILL_W     46
#define SCALE_PILL_X 16
#define ONOFF_PILL_X 178
#define PILL_PAD   6            // extra tap margin around the pills
#define JIG_ZONE_PAD 10         // letter box + this = the ON/OFF tap zone
