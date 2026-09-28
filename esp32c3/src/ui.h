#pragma once

// Render task: draws the active screen into fb and pushes it to the panel.
void ui_task(void *);

// Hit areas shared by drawing and touch handling. The panel is a 240 px
// circle, so everything sits inside radius ~115 around (120,120).
// Layout source of truth: https://claude.ai/artifact/QbZAqAnnHaYKeZfYejQRWz

// Clipboard
#define BTN_Y       166
#define BTN_H       40
#define BTN_W       79
#define BTN_COPY_X  38
#define BTN_PASTE_X 123

// Jiggler
#define JIG_CX 120
#define JIG_CY 120
#define JIG_R  57

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
