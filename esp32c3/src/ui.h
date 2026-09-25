#pragma once

// Render task: draws the active screen into fb and pushes it to the panel.
void ui_task(void *);

// Hit areas shared by drawing and touch handling. The panel is a 240 px
// circle, so everything sits inside radius ~115 around (120,120).
#define BTN_Y       166
#define BTN_H       44
#define BTN_W       78
#define BTN_COPY_X  40
#define BTN_PASTE_X 122

// Settings: the cog (tap to open Bluetooth).
#define COG_CX 120
#define COG_CY 98
#define COG_HIT_R 62

// Bluetooth page: back chevron and one or two buttons.
#define BACK_HIT_X 100   // x < this and y < BACK_HIT_Y
#define BACK_HIT_Y 56
#define BT_BTN_Y   170
#define BT_BTN_H   38
#define BT_BTN1_X  70    // single centred button
#define BT_BTN1_W  100

#define JIG_CX 120
#define JIG_CY 110
#define JIG_R  58
