// Round-screen pages, built to the approved mockup
// (https://claude.ai/artifact/QbZAqAnnHaYKeZfYejQRWz), drawn from a ui_state_t
// only: no Arduino, no clock, no shared app state. ui.cpp calls it on the
// device; hostui/ compiles it for the PC app's device mirror. One accent colour
// carries the output mode everywhere: amber = PC, blue = Bluetooth.
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "board.h"
#include "gfx.h"
#include "icons.h"
#include "jig_lane.h"
#include "ui.h"
#include "ui_pages.h"

// The top chip's name for wired output: "PC" on the ESP32-C3 (the app types for
// it); a board that is itself a USB keyboard/mouse (RP2350) builds with "USB".
#ifndef UI_PC_LABEL
#define UI_PC_LABEL "PC"
#endif

#define C_BG      RGB(7, 9, 13)
#define C_SURF    RGB(20, 26, 36)
#define C_SURF2   RGB(28, 36, 50)
#define C_INNER   RGB(16, 21, 30)
#define C_TEXT    RGB(232, 236, 242)
#define C_CLIP    RGB(201, 209, 220)
#define C_DIM     RGB(138, 148, 166)
#define C_FAINT   RGB(74, 83, 102)
#define C_DOT_OFF RGB(58, 66, 82)
#define C_BT      RGB(76, 141, 255)
#define C_PC      RGB(242, 163, 58)
#define C_OK      RGB(60, 203, 127)
#define C_BAD     RGB(229, 72, 77)
#define C_BAD_TXT RGB(255, 138, 141)
#define C_BT_TINT RGB(18, 30, 52)    // 16% blue over C_BG
#define C_PC_TINT RGB(45, 34, 20)    // 16% amber over C_BG

static uint16_t accent(const ui_state_t *s) { return s->bt_mode ? C_BT : C_PC; }

// Centred text with its capitals vertically centred on cy.
static void text_c(int cy, const char *str, const aa_font_t *f, uint16_t col, int spacing) {
    gfx_text_aa_centered(LCD_W / 2, gfx_text_aa_ytop(f, cy), str, f, col, spacing);
}

static void text_at(int x, int cy, const char *str, const aa_font_t *f, uint16_t col, int spacing) {
    gfx_text_aa(x, gfx_text_aa_ytop(f, cy), str, f, col, spacing);
}

static void pill(int x, int y, int w, int h, uint16_t col) { gfx_rrect(x, y, w, h, h / 2.f, col); }

// Pill button: optional leading/trailing icon around a centred label.
static void button(int x, int y, int w, int h, uint16_t bg, uint16_t fg, const char *label,
                   icon_fn lead, icon_fn trail) {
    pill(x, y, w, h, bg);
    const aa_font_t *f = &font_button;
    const int iw = 10, gap = 4, tw = gfx_text_aa_width(label, f, 0);
    int total = tw + (lead ? iw + gap : 0) + (trail ? iw + gap : 0);
    int cx = x + (w - total) / 2, cy = y + h / 2;
    if (lead) { lead(cx + iw / 2.f, cy, iw, fg); cx += iw + gap; }
    text_at(cx, cy, label, f, fg, 0);
    cx += tw + gap;
    if (trail) trail(cx + iw / 2.f, cy, iw, fg);
}

// Transient message if one is showing, else the given text.
static const char *msg_or(const ui_state_t *s, const char *normal) { return s->msg[0] ? s->msg : normal; }

// Edge ring + top chip: where output goes, and whether that link is live.
static void frame(const ui_state_t *s) {
    gfx_fill(C_BG);
    gfx_ring(120.f, 120.f, 118.5f, 3.f, accent(s));
    int bt = s->bt_mode;
    const char *label = bt ? "BLUETOOTH" : UI_PC_LABEL;
    int tw = gfx_text_aa_width(label, &font_caps, 1), w = 8 + 10 + 4 + tw + 5 + 5 + 8, x = 120 - w / 2, y = 16;
    pill(x, y, w, 18, bt ? C_BT_TINT : C_PC_TINT);
    (bt ? icon_bt : icon_monitor)(x + 13.f, y + 9.f, 10.f, accent(s));
    text_at(x + 22, y + 9, label, &font_caps, accent(s), 1);
    gfx_disc(x + w - 10.5f, y + 9.f, 2.6f, s->link_ok ? C_OK : C_BAD);
}

static void dots(int count, int active) {
    for (int i = 0; i < count; i++)
        gfx_disc(120.f + (i * 2 - (count - 1)) * 6.f, 229.f, 2.6f, i == active ? C_TEXT : C_DOT_OFF);
}

static void draw_clip(const ui_state_t *s) {
    enum { CX0 = 32, CY0 = 52, CW = 176, CH = 80, ROWS = 5, PITCH = 12 };
    const int cols = (CW - 16) / font_mono.glyphs['M' - ' '].adv;
    char info[40];
    int pasting = s->clip_state == CLIP_PASTING;
    text_c(TITLE_Y, "Clipboard", &font_title, C_TEXT, 0);

    int len = s->clip_len, view = len < UI_CLIP_VIEW ? len : UI_CLIP_VIEW;
    // Trash: clears the clip; live only when there is text and no paste is typing.
    icon_trash(TRASH_CX, TRASH_CY, 18.f, len && !pasting ? C_TEXT : C_FAINT);
    gfx_rrect(CX0, CY0, CW, CH, 12.f, len ? C_SURF : C_INNER);
    if (len == 0) {
        text_c(CY0 + 32, "Select text on PC,", &font_body, C_DIM, 0);
        text_c(CY0 + 48, "then tap Copy", &font_body, C_DIM, 0);
    } else {
        // Wrap into rows of monospaced text, drawn a row at a time.
        char line[40];
        int row = 0, col = 0;
        for (int i = 0; i <= view && row < ROWS; i++) {
            char c = i < view ? s->clip[i] : '\n';
            if (c == '\r') continue;
            if (c == '\n' || col == cols) {
                line[col] = 0;
                gfx_text_aa(CX0 + 8, CY0 + 5 + row * PITCH, line, &font_mono, C_CLIP, 0);
                row++;
                col = 0;
                if (c == '\n') continue;
            }
            line[col++] = c == '\t' ? ' ' : c;
        }
    }
    snprintf(info, sizeof info, len ? "%d chars from %s" : "Empty", len, s->clip_src);

    button(BTN_COPY_X, BTN_Y, BTN_W, BTN_H, C_SURF2, C_TEXT,
           s->clip_state == CLIP_COPYING ? "..." : "Copy", icon_copy, NULL);
    if (pasting) button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, C_BAD, C_BG, "Stop", NULL, NULL);
    else button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, len ? accent(s) : C_SURF2, len ? C_BG : C_FAINT,
                "Paste", NULL, icon_arrow_right);
    if (pasting && len) {
        gfx_rrect(48, CLIP_STATUS_Y - 2, 144, 4, 2.f, C_SURF2);
        gfx_rrect(48, CLIP_STATUS_Y - 2, 144 * s->paste_pos / len + 1, 4, 2.f, accent(s));
    } else {
        text_c(CLIP_STATUS_Y, msg_or(s, info), &font_body, C_DIM, 0);
    }
}

static void jig_pill(int x, const char *label, uint16_t bg, uint16_t fg, uint16_t ring) {
    pill(x, PILL_Y, PILL_W, PILL_H, ring);
    if (ring != bg) pill(x + 1, PILL_Y + 1, PILL_W - 2, PILL_H - 2, bg);
    gfx_text_aa_centered(x + PILL_W / 2, gfx_text_aa_ytop(&font_caps, PILL_Y + PILL_H / 2), label, &font_caps, fg, 1);
}

static void draw_jig(const ui_state_t *s) {
    char str[40], sc[8];
    int on = s->jig_on, live = on || s->jig_demo, bt = s->bt_mode;
    int scale = s->jig_scale >= 0 && s->jig_scale < JIG_SCALE_COUNT ? s->jig_scale : 0;
    int letter = s->jig_letter >= 0 && s->jig_letter < JIG_PATH_COUNT ? s->jig_letter : 0;
    text_c(TITLE_Y, "Jiggler", &font_title, C_TEXT, 0);
    snprintf(sc, sizeof sc, "%.1fX", (double)JIG_SCALES[scale]);
    jig_pill(SCALE_PILL_X, sc, C_SURF2, accent(s), C_SURF2);
    jig_pill(ONOFF_PILL_X, on ? "ON" : "OFF", on ? (bt ? C_BT_TINT : C_PC_TINT) : C_SURF2, on ? accent(s) : C_DIM,
             on ? accent(s) : C_SURF2);
    jig_draw_lane(&JIG_PATHS[letter], live ? accent(s) : C_SURF2, C_INNER);
    if (live) {
        float x, y;
        jig_dot_screen(s->jig_x, s->jig_y, &x, &y);
        gfx_disc(x, y, JIG_DOT, accent(s));
    }

    const char *status = "Tap to start";
    uint16_t scol = C_TEXT;
    if (on && !s->link_ok) { status = s->down_reason; scol = C_BAD_TXT; }
    else if (on && s->jig_paused) status = "Paused: pasting";
    else if (on && s->jig_phase == JIG_MOVING) {
        snprintf(str, sizeof str, "Next menu in %ds", s->jig_next_s < 0 ? 0 : (int)s->jig_next_s);
        status = str;
    } else if (on && (s->jig_phase == JIG_CLICK_DOWN || s->jig_phase == JIG_MENU_OPEN)) status = "Right-click menu";
    else if (on && s->jig_phase == JIG_ESC_DOWN) status = "Esc";
    else if (on) status = "Pausing";
    text_c(190, status, &font_label, scol, 0);

    char stats[40];
    if (on) {
        uint32_t up = (uint32_t)s->jig_up_s;
        snprintf(stats, sizeof stats, "%lu menus %02lu:%02lu:%02lu", (unsigned long)s->jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
    } else {
        strcpy(stats, "Menu every 45-150s");
    }
    text_c(206, msg_or(s, stats), &font_body, C_DIM, 0);
}

static const char *bt_row_status(const ui_state_t *s, uint16_t *col) {
    *col = C_DIM;
    switch (s->bt_state) {
    case UI_BT_CONNECTED: *col = C_OK; return "Connected";
    case UI_BT_PAIRING: return "Pairing...";
    case UI_BT_WAITING: return "Paired";
    case UI_BT_OFF: return "Paired, off";
    default: return "Not paired";
    }
}

static void draw_settings(const ui_state_t *s) {
    int tw = gfx_text_aa_width("Settings", &font_title, 0), x0 = 120 - (15 + 6 + tw) / 2;
    icon_cog(x0 + 7.5f, (float)TITLE_Y, 15.f, C_DIM);
    text_at(x0 + 21, TITLE_Y, "Settings", &font_title, C_TEXT, 0);
    text_c(65, "OUTPUT", &font_caps, C_DIM, 2);

    int avail = s->bt_avail, bt = s->bt_mode;
    pill(38, 74, 164, 40, C_SURF);
    button(SEG_BT_X, SEG_Y, SEG_W, SEG_H, bt ? C_BT : C_SURF, bt ? C_BG : (avail ? C_DIM : C_FAINT),
           "Bluetooth", avail ? icon_bt : icon_lock, NULL);
    button(SEG_PC_X, SEG_Y, SEG_W, SEG_H, bt ? C_SURF : C_PC, bt ? C_DIM : C_BG,
           "PC", icon_monitor, NULL);
    const char *cap = !avail ? "Pair Bluetooth to switch" : (bt ? "Sends to Bluetooth host" : "Sends to this PC");
    text_c(126, msg_or(s, cap), &font_body, C_DIM, 0);

    uint16_t scol;
    const char *st = bt_row_status(s, &scol);
    gfx_rrect(ROW_X, ROW_Y, ROW_W, ROW_H, 14.f, C_SURF);
    gfx_disc(55.f, 162.f, 13.f, RGB(28, 42, 67));
    icon_bt(55.f, 162.f, 13.f, C_BT);
    text_at(75, 154, "Bluetooth", &font_label, C_TEXT, 0);
    text_at(75, 170, st, &font_body, scol, 0);
    icon_chevron_right(193.f, 162.f, 11.f, C_DIM);
}

static void draw_bt(const ui_state_t *s) {
    char str[40];
    int st = s->bt_state;
    gfx_disc(BACK_CX, BACK_CY, 14.f, C_SURF);
    icon_chevron_left(BACK_CX - 1.f, BACK_CY, 12.f, C_TEXT);
    text_c(49, "Bluetooth", &font_title, C_TEXT, 0);

    if (st == UI_BT_UNPAIRED) {
        gfx_disc(120.f, 94.f, 26.f, C_BT_TINT);
        icon_bt(120.f, 94.f, 22.f, C_BT);
        text_c(134, "Not paired", &font_title, C_TEXT, 0);
        text_c(152, "PC: Settings > Bluetooth >", &font_body, C_DIM, 0);
        text_c(166, msg_or(s, "Add device > Touch Deck"), &font_body, C_DIM, 0);
        button(BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H, C_BT, C_BG, "Pair", NULL, NULL);
    } else if (st == UI_BT_PAIRING) {
        text_c(72, "Enter this PIN on your PC", &font_body, C_DIM, 0);
        snprintf(str, sizeof str, "%06lu", (unsigned long)s->bt_passkey);
        for (int i = 0; i < 6; i++) {
            int bx = 47 + i * 24 + (i >= 3 ? 5 : 0);
            gfx_rrect(bx, 84, 21, 30, 6.f, C_SURF);
            char d[2] = {str[i], 0};
            gfx_text_aa_centered(bx + 11, gfx_text_aa_ytop(&font_pin, 99), d, &font_pin, C_TEXT, 0);
        }
        text_c(130, msg_or(s, "Pick \"Touch Deck\" in Add device"), &font_body, C_DIM, 0);
        int left = s->bt_secs_left;
        snprintf(str, sizeof str, "Expires in %d:%02d", left / 60, left % 60);
        text_c(146, str, &font_body, C_BT, 0);
        button(BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H, C_SURF2, C_TEXT, "Cancel", NULL, NULL);
    } else {
        int conn = st == UI_BT_CONNECTED, off = st == UI_BT_OFF;
        uint16_t dot = conn ? C_OK : C_DIM;
        gfx_disc(120.f, 92.f, 25.f, C_SURF);
        icon_monitor(120.f, 91.f, 22.f, C_TEXT);
        gfx_disc(139.f, 110.f, 6.5f, C_BG);
        gfx_disc(139.f, 110.f, 4.5f, dot);
        text_c(127, conn ? "CONNECTED" : (off ? "DISCONNECTED" : "WAITING"), &font_caps, dot, 1);
        snprintf(str, sizeof str, "%.18s", s->bt_host[0] ? s->bt_host : "Paired PC");
        text_c(145, str, &font_title, C_TEXT, 0);
        const char *sub = conn ? (s->bt_ready ? "Keyboard + mouse" : "Connecting...")
                        : off ? "Tap Connect to resume" : "PC will reconnect";
        text_c(163, msg_or(s, sub), &font_body, C_DIM, 0);
        if (off) button(BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_OK, C_BG, "Connect", NULL, NULL);
        else button(BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_SURF2, C_TEXT, "Disconnect", NULL, NULL);
        pill(BT_BTN_R_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_BAD);
        pill(BT_BTN_R_X + 1, BT_BTN2_Y + 1, BT_BTN2_W - 2, BT_BTN2_H - 2, C_BG);
        gfx_text_aa_centered(BT_BTN_R_X + BT_BTN2_W / 2, gfx_text_aa_ytop(&font_button, BT_BTN2_Y + BT_BTN2_H / 2),
                             "Forget", &font_button, C_BAD_TXT, 0);
    }
}

void ui_draw_page(const ui_state_t *s) {
    frame(s);
    if (s->sub) { draw_bt(s); dots(1, 0); }
    else {
        if (s->screen == SCR_CLIP) draw_clip(s);
        else if (s->screen == SCR_JIG) draw_jig(s);
        else draw_settings(s);
        dots(UI_PAGE_COUNT, s->screen);
    }
}

// Screen box around the dot at box position (bx, by), wide enough for its anti-aliased edge.
rect_t ui_dot_rect(float bx, float by) {
    float x, y;
    jig_dot_screen(bx, by, &x, &y);
    int r = (int)JIG_DOT + 2;
    rect_t b = {(int)x - r, (int)y - r, 2 * r + 2, 2 * r + 2};
    return b;
}

rect_t ui_rect_union(rect_t a, rect_t b) {
    int x0 = a.x < b.x ? a.x : b.x, y0 = a.y < b.y ? a.y : b.y;
    int x1 = a.x + a.w > b.x + b.w ? a.x + a.w : b.x + b.w, y1 = a.y + a.h > b.y + b.h ? a.y + a.h : b.y + b.h;
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > LCD_W) x1 = LCD_W;
    if (y1 > LCD_H) y1 = LCD_H;
    rect_t r = {x0, y0, x1 - x0, y1 - y0};
    return r;
}
