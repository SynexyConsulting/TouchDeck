#pragma once

#include "ui_state.h"

// Render task: draws the active screen into fb and pushes it to the panel.
void ui_task(void *);
#ifdef __cplusplus
// Samples the live state for drawing or for the mirror sync; with_clip = false
// leaves the clip text out.
void ui_state_fill(ui_state_t *s, bool with_clip);
#endif

// Hit areas shared by drawing and touch handling. The panel is a 240 px
// circle, so everything sits inside radius ~115 around (120,120).
// Layout source of truth: https://claude.ai/artifact/QbZAqAnnHaYKeZfYejQRWz

#define TITLE_Y     42          // page titles (centre of capitals); the top chip ends at y 34

// Clipboard: text box, then the buttons, then the status line.
#define BTN_Y       140
#define BTN_H       40
#define BTN_W       79
#define BTN_COPY_X  38
#define BTN_PASTE_X 123
#define CLIP_STATUS_Y 196
#define TRASH_CX    178
#define TRASH_CY    42
#define TRASH_HIT   18          // half-size of the square tap target

// Jiggler: pills either side of the letter lane (jig_paths.h geometry).
#define PILL_Y      111
#define PILL_H      18
#define PILL_W      44
#define SCALE_PILL_X 10
#define ONOFF_PILL_X 186
#define PILL_PAD    6
#define JIG_ZONE_PAD 10         // letter box + this = the ON/OFF tap zone

// Jiggler settings page: four rows (context menu, key, menu open, pause), a label on
// the left and its control on the right; a hint line below. Shared by drawing and
// touch handling (main.c / main.cpp), same names on every board.
#define JS_ROW_Y(i)  (JS_ROW0_Y + (i) * JS_ROW_DY)   // row centre
#define JS_ROW0_Y    76           // rows stay inside the circle's chord (x 30..208)
#define JS_ROW_DY    32
#define JS_LABEL_X   30
#define JS_CTRL_H    22
#define JS_TOGGLE_X  160
#define JS_TOGGLE_W  48
#define JS_SEG_W     40
#define JS_SEG_ESC_X 126
#define JS_SEG_F15_X 168
#define JS_STEP_W    26
#define JS_MINUS_X   126
#define JS_PLUS_X    182
#define JS_HINT_Y    202
#define JS_HIT_PAD   6
#define JS_CLOSE_CX  50           // the X that closes the panel, left of the title
#define JS_CLOSE_CY  48
#define JIG_COG_CX   40           // the cog on the Jiggler page (lower left) that opens it
#define JIG_COG_CY   188
#define JS_ICON_HIT  20           // half-size of the cog's and the X's tap squares

// Settings: output toggle segments and the Bluetooth row
#define SEG_Y     77
#define SEG_H     34
#define SEG_W     78
#define SEG_BT_X  41
#define SEG_PC_X  121
#define ROW_X     34
#define ROW_Y     140
#define ROW_W     172
#define ROW_H     44

// Bluetooth page
#define BACK_CX    54
#define BACK_CY    48
#define BACK_HIT_R 22
#define BT_BTN1_X  66    // single centred button (Pair / Cancel)
#define BT_BTN1_Y  176
#define BT_BTN1_W  108
#define BT_BTN1_H  36
#define BT_BTN2_Y  178   // two buttons (Disconnect|Connect, Forget)
#define BT_BTN2_H  34
#define BT_BTN2_W  77
#define BT_BTN_L_X 40
#define BT_BTN_R_X 123
