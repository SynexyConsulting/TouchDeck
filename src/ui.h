#pragma once
#include "touch.h"
#include "ui_state.h"

// Core1 entry point: renders whichever screen is active and pushes frames.
void ui_core1_main(void);
// Samples the live state (app) for drawing or for the mirror sync; with_clip = 0
// leaves the clip text out. Safe from either core.
void ui_state_fill(ui_state_t *s, int with_clip);

#ifdef TD_ROUND
#include "round/ui.h"   // the RP2350 round board uses the ESP32-C3's round geometry
#else
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

// Jiggler settings page: four rows (context menu, key, menu open, pause), a label on
// the left and its control on the right; a hint line below. Shared by drawing and
// touch handling (main.c / main.cpp), same names on every board.
#define JS_ROW_Y(i)  (JS_ROW0_Y + (i) * JS_ROW_DY)   // row centre
#define JS_ROW0_Y    84
#define JS_ROW_DY    40
#define JS_LABEL_X   22
#define JS_CTRL_H    24
#define JS_TOGGLE_X  164
#define JS_TOGGLE_W  54
#define JS_SEG_W     44
#define JS_SEG_ESC_X 128
#define JS_SEG_F15_X 174
#define JS_STEP_W    30
#define JS_MINUS_X   124
#define JS_PLUS_X    188
#define JS_HINT_Y    240
#define JS_HIT_PAD   8            // extra tap margin around each control
#define JS_CLOSE_CX  30           // the X that closes the panel, left of the title
#define JS_CLOSE_CY  42
#define JIG_COG_CX   30           // the cog on the Jiggler page (lower left) that opens it
#define JIG_COG_CY   230
#define JS_ICON_HIT  20           // half-size of the cog's and the X's tap squares
#endif   // TD_ROUND
