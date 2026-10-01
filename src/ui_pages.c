// The RP2040 Touch Deck's pages (watch, clipboard, jiggler, jiggler settings), drawn from a
// ui_state_t only: no SDK, no clock, no shared app state. ui.c calls it on the
// device; hostui/ compiles it for the PC app's device mirror.
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "board.h"
#include "gfx.h"
#include "icons.h"
#include "jig_lane.h"
#include "jig_menu.h"
#include "ui.h"
#include "ui_pages.h"

#define CX (LCD_W / 2.f)
#define CY (LCD_H / 2.f)
// Rounded-square watch face filling the 240x280 panel; its corners follow the
// panel's 44 px glass radius (see frame_usb), inset by the face margin.
#define FACE_M 6
#define FACE_R (44.f - FACE_M)
#define TICK_IN 9       // ticks start this far inside the face edge
#define DEG2RAD 0.017453292f

#define COL_DIAL   RGB(18, 24, 38)
#define COL_RIM    RGB(90, 100, 120)
#define COL_MARK   RGB(200, 205, 215)
#define COL_HAND   RGB(240, 240, 240)
#define COL_SECOND RGB(255, 70, 40)
#define COL_TEXT   RGB(230, 232, 238)
#define COL_DIM    RGB(120, 128, 145)
#define COL_STOP   RGB(210, 60, 50)
#define COL_OK     RGB(60, 200, 110)

// Touch Deck design language (shared with esp32c3/src/ui_pages.c). This board
// only outputs over USB to this PC, so the accent is always amber (PC).
#define C_BG      RGB(7, 9, 13)
#define C_SURF    RGB(20, 26, 36)
#define C_SURF2   RGB(28, 36, 50)
#define C_INNER   RGB(16, 21, 30)
#define C_TEXT    RGB(232, 236, 242)
#define C_CLIP    RGB(201, 209, 220)
#define C_DIM     RGB(138, 148, 166)
#define C_FAINT   RGB(74, 83, 102)
#define C_PC      RGB(242, 163, 58)
#define C_OK      RGB(60, 203, 127)
#define C_BAD     RGB(229, 72, 77)
#define C_BAD_TXT RGB(255, 138, 141)
#define C_PC_TINT RGB(45, 34, 20)     // 16% amber over C_BG

// ---------- watch ----------

static void polar(float cx, float cy, float deg, float len, float *x, float *y) {
    float a = deg * DEG2RAD;
    *x = cx + sinf(a) * len;
    *y = cy - cosf(a) * len;
}

rect_t ui_rect_union(rect_t a, rect_t b) {
    int x0 = a.x < b.x ? a.x : b.x, y0 = a.y < b.y ? a.y : b.y;
    int x1 = a.x + a.w > b.x + b.w ? a.x + a.w : b.x + b.w;
    int y1 = a.y + a.h > b.y + b.h ? a.y + a.h : b.y + b.h;
    rect_t r = {x0, y0, x1 - x0, y1 - y0};
    return r;
}

// The three hands at time t (seconds of day): one definition for drawing them
// and for working out what a per-second partial redraw has to cover.
typedef struct { float deg, tail, len, thick; uint16_t col; } hand_t;

static void watch_hands(int t, hand_t out[3]) {
    int h = t / 3600, m = (t / 60) % 60, s = t % 60;
    hand_t hh = {((h % 12) + m / 60.f + s / 3600.f) * 30.f, 12.f, 58.f, 8.f, COL_HAND};
    hand_t mh = {(m + s / 60.f) * 6.f, 14.f, 90.f, 5.f, COL_HAND};
    hand_t sh = {s * 6.f, 22.f, 100.f, 2.f, COL_SECOND};
    out[0] = hh; out[1] = mh; out[2] = sh;
}

// Pixels the hands (and the centre cap) can touch at time t.
rect_t ui_hands_rect(int t) {
    hand_t hs[3];
    watch_hands(t, hs);
    float x0 = CX - 8.f, y0 = CY - 8.f, x1 = CX + 8.f, y1 = CY + 8.f;   // centre discs
    for (int i = 0; i < 3; i++) {
        float ax, ay, bx, by, m = hs[i].thick * 0.5f + 2.f;
        polar(CX, CY, hs[i].deg + 180.f, hs[i].tail, &ax, &ay);
        polar(CX, CY, hs[i].deg, hs[i].len, &bx, &by);
        x0 = fminf(x0, fminf(ax, bx) - m); y0 = fminf(y0, fminf(ay, by) - m);
        x1 = fmaxf(x1, fmaxf(ax, bx) + m); y1 = fmaxf(y1, fmaxf(ay, by) + m);
    }
    rect_t r = {(int)floorf(x0), (int)floorf(y0), (int)ceilf(x1 - x0) + 1, (int)ceilf(y1 - y0) + 1};
    return r;
}

// The stopwatch's black box below the centre.
rect_t ui_stopwatch_rect(void) {
    int tw = gfx_text_aa_width("88:88:88", &font_timer, 0), bw = tw + 16, bh = font_timer.cap_h + 14;
    int cy = (int)CY + 50;
    rect_t r = {(int)CX - bw / 2, cy - bh / 2, bw, bh};
    return r;
}

static void hand(float deg, float tail, float len, float thick, uint16_t col) {
    float x0, y0, x1, y1;
    polar(CX, CY, deg + 180.f, tail, &x0, &y0);
    polar(CX, CY, deg, len, &x1, &y1);
    gfx_line(x0, y0, x1, y1, thick, col);
}

// Speaker glyph: body + flared cone, then sound waves or a red X.
static void draw_mute_icon(int muted) {
    uint16_t col = muted ? COL_DIM : COL_TEXT;
    int x = MUTE_CX - 11, y = MUTE_CY;
    gfx_rect(x, y - 3, 4, 7, col);
    for (int i = 0; i < 6; i++)                     // cone widens to the right
        gfx_rect(x + 4 + i, y - 3 - i, 1, 7 + 2 * i, col);
    if (muted) {
        gfx_line(MUTE_CX + 4, y - 5, MUTE_CX + 12, y + 5, 2.f, COL_STOP);
        gfx_line(MUTE_CX + 4, y + 5, MUTE_CX + 12, y - 5, 2.f, COL_STOP);
    } else {
        for (int k = 0; k < 2; k++) {               // two arcs as short chords
            float r = 6.f + 5.f * k;
            float px = 0, py = 0;
            for (int a = -45; a <= 45; a += 15) {
                float qx = MUTE_CX - 1 + r * cosf(a * DEG2RAD), qy = y + r * sinf(a * DEG2RAD);
                if (a > -45) gfx_line(px, py, qx, qy, 1.6f, col);
                px = qx; py = qy;
            }
        }
    }
}

// Distance from the centre, along the direction of minute i, to the rounded
// square the ticks sit on (bisection on the rounded-box distance). Computed once.
static float tick_reach[60];

static float rounded_box_sd(float px, float py, float hx, float hy, float r) {
    float qx = fabsf(px) - (hx - r), qy = fabsf(py) - (hy - r);
    float ox = fmaxf(qx, 0.f), oy = fmaxf(qy, 0.f);
    return sqrtf(ox * ox + oy * oy) + fminf(fmaxf(qx, qy), 0.f) - r;
}

static void tick_reach_init(void) {
    float hx = LCD_W / 2.f - FACE_M - TICK_IN, hy = LCD_H / 2.f - FACE_M - TICK_IN, r = FACE_R - TICK_IN;
    for (int i = 0; i < 60; i++) {
        float a = i * 6.f * DEG2RAD, dx = sinf(a), dy = -cosf(a), lo = 0.f, hi = 200.f;
        for (int k = 0; k < 24; k++) {
            float mid = (lo + hi) * 0.5f;
            if (rounded_box_sd(dx * mid, dy * mid, hx, hy, r) < 0.f) lo = mid; else hi = mid;
        }
        tick_reach[i] = lo;
    }
}

static void draw_watch(const ui_state_t *s) {
    static int ticks_ready;
    if (!ticks_ready) { tick_reach_init(); ticks_ready = 1; }

    gfx_rrect(FACE_M, FACE_M, LCD_W - 2 * FACE_M, LCD_H - 2 * FACE_M, FACE_R, COL_DIAL);
    gfx_rrect_ring(FACE_M, FACE_M, LCD_W - 2 * FACE_M, LCD_H - 2 * FACE_M, FACE_R, 3.f, COL_RIM);

    for (int i = 0; i < 60; i++) {
        float x0, y0, x1, y1;
        int hour = (i % 5) == 0;
        polar(CX, CY, i * 6.f, tick_reach[i] - (hour ? 15.f : 6.f), &x0, &y0);
        polar(CX, CY, i * 6.f, tick_reach[i], &x1, &y1);
        gfx_line(x0, y0, x1, y1, hour ? ((i % 15) == 0 ? 6.f : 4.f) : 1.5f, COL_MARK);
    }
    if (s->helper)
        gfx_text_aa_centered((int)CX, gfx_text_aa_ytop(&font_caps, 84), "PC", &font_caps, COL_DIM, 1);
    draw_mute_icon(s->muted);

    // Stopwatch (BOOT button): black box below the centre, over the dial but
    // under the hands. 7-segment digits at twice the chip label's size.
    {
        char st[12];
        int ts = s->timer_s;
        snprintf(st, sizeof st, "%02d:%02d:%02d", (ts / 3600) % 100, ts / 60 % 60, ts % 60);
        rect_t box = ui_stopwatch_rect();
        int bh = box.h, cy = box.y + box.h / 2;
        gfx_rrect(box.x, box.y, box.w, box.h, 6.f, RGB(0, 0, 0));
        gfx_text_aa_centered((int)CX, gfx_text_aa_ytop(&font_timer, cy), st, &font_timer, RGB(255, 255, 255), 0);
        if (s->jig_on)       // tucked just under the stopwatch
            gfx_text_aa_centered((int)CX, gfx_text_aa_ytop(&font_caps, cy + bh / 2 + 8), "JIGGLING",
                                 &font_caps, COL_OK, 1);
    }

    hand_t hs[3];
    watch_hands(s->time_s, hs);
    for (int i = 0; i < 3; i++) hand(hs[i].deg, hs[i].tail, hs[i].len, hs[i].thick, hs[i].col);
    gfx_disc(CX, CY, 6.f, COL_SECOND);
    gfx_disc(CX, CY, 2.f, COL_DIAL);
}

// ---------- shared pieces ----------

static void text_c(int cx, int cy, const char *s, const aa_font_t *f, uint16_t col, int spacing) {
    gfx_text_aa_centered(cx, gfx_text_aa_ytop(f, cy), s, f, col, spacing);
}

static void text_at(int x, int cy, const char *s, const aa_font_t *f, uint16_t col, int spacing) {
    gfx_text_aa(x, gfx_text_aa_ytop(f, cy), s, f, col, spacing);
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

// Edge ring + top chip on every screen: output goes to USB, dot = enumerated.
static void frame_usb(const ui_state_t *s) {
    // Corner radius matched on the device to the panel's rounded glass (40 clipped, 46 was loose).
    gfx_rrect_ring(0, 0, LCD_W, LCD_H, 44.f, 3.f, C_PC);
    const char *label = "USB";
    int tw = gfx_text_aa_width(label, &font_caps, 1), w = 8 + 10 + 4 + tw + 5 + 5 + 8, x = 120 - w / 2, y = 5;
    pill(x, y, w, 18, C_PC_TINT);
    icon_monitor(x + 13.f, y + 9.f, 10.f, C_PC);
    text_at(x + 22, y + 9, label, &font_caps, C_PC, 1);
    gfx_disc(x + w - 10.5f, y + 9.f, 2.6f, s->link_ok ? C_OK : C_BAD);
}

// ---------- clipboard ----------

static void draw_clip(const ui_state_t *s) {
    enum { CX0 = 20, CY0 = 58, CW = 200, CH = 110, ROWS = 8, PITCH = 12 };
    const int cols = (CW - 16) / font_mono.glyphs['M' - ' '].adv;
    char info[40];
    int pasting = s->clip_state == CLIP_PASTING;
    text_c(LCD_W / 2, TITLE_Y, "Clipboard", &font_title, C_TEXT, 0);

    int len = s->clip_len, view = len < UI_CLIP_VIEW ? len : UI_CLIP_VIEW;
    // Trash: clears the clip; live only when there is text and no paste is typing.
    icon_trash(TRASH_CX, TRASH_CY, 18.f, len && !pasting ? C_TEXT : C_FAINT);
    gfx_rrect(CX0, CY0, CW, CH, 12.f, len ? C_SURF : C_INNER);
    if (len == 0) {
        text_c(LCD_W / 2, CY0 + 44, "Select text on PC,", &font_body, C_DIM, 0);
        text_c(LCD_W / 2, CY0 + 60, "then tap Copy", &font_body, C_DIM, 0);
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
    else button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, len ? C_PC : C_SURF2, len ? C_BG : C_FAINT,
                "Paste", NULL, icon_arrow_right);
    if (pasting && len) {
        gfx_rrect(40, CLIP_STATUS_Y - 2, 160, 4, 2.f, C_SURF2);
        gfx_rrect(40, CLIP_STATUS_Y - 2, 160 * s->paste_pos / len + 1, 4, 2.f, C_PC);
    } else {
        text_c(LCD_W / 2, CLIP_STATUS_Y, msg_or(s, info), &font_body, C_DIM, 0);
    }
}

// ---------- jiggler ----------

static void jig_pill(int x, const char *label, uint16_t bg, uint16_t fg, uint16_t ring) {
    pill(x, PILL_Y, PILL_W, PILL_H, ring);
    if (ring != bg) pill(x + 1, PILL_Y + 1, PILL_W - 2, PILL_H - 2, bg);
    text_c(x + PILL_W / 2, PILL_Y + PILL_H / 2, label, &font_caps, fg, 1);
}

static void draw_jig(const ui_state_t *s) {
    char str[40], sc[8];
    int on = s->jig_on, live = on || s->jig_demo;
    int scale = s->jig_scale >= 0 && s->jig_scale < JIG_SCALE_COUNT ? s->jig_scale : 0;
    int letter = s->jig_letter >= 0 && s->jig_letter < JIG_PATH_COUNT ? s->jig_letter : 0;
    text_c(LCD_W / 2, TITLE_Y, "Jiggler", &font_title, C_TEXT, 0);
    snprintf(sc, sizeof sc, "%.1fX", (double)JIG_SCALES[scale]);
    jig_pill(SCALE_PILL_X, sc, C_SURF2, C_PC, C_SURF2);
    jig_pill(ONOFF_PILL_X, on ? "ON" : "OFF", on ? C_PC_TINT : C_SURF2, on ? C_PC : C_DIM, on ? C_PC : C_SURF2);
    jig_draw_lane(&JIG_PATHS[letter], live ? C_PC : C_SURF2, C_INNER);
    if (live) {
        float x, y;
        jig_dot_screen(s->jig_x, s->jig_y, &x, &y);
        gfx_disc(x, y, JIG_DOT, C_PC);
    }

    const char *status = "Tap to start";
    uint16_t scol = C_TEXT;
    if (on && !s->link_ok) { status = "Plug into USB"; scol = C_BAD_TXT; }
    else if (on && s->jig_paused) status = "Paused: pasting";
    else if (on && s->jig_phase == JIG_MOVING) {
        snprintf(str, sizeof str, "Next menu in %ds", s->jig_next_s < 0 ? 0 : (int)s->jig_next_s);
        status = str;
    } else if (on && (s->jig_phase == JIG_CLICK_DOWN || s->jig_phase == JIG_MENU_OPEN)) status = "Right-click menu";
    else if (on && s->jig_phase == JIG_ESC_DOWN) status = s->jig_key ? "F15" : "Esc";
    else if (on) status = "Pausing";
    text_c(LCD_W / 2, 220, status, &font_label, scol, 0);

    char stats[40];
    if (on) {
        uint32_t up = (uint32_t)s->jig_up_s;
        snprintf(stats, sizeof stats, "%lu menus %02lu:%02lu:%02lu", (unsigned long)s->jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
    } else {
        strcpy(stats, "Menu every 45-150s");
    }
    text_c(LCD_W / 2, 238, msg_or(s, stats), &font_body, C_DIM, 0);
    icon_cog(JIG_COG_CX, JIG_COG_CY, 20.f, C_DIM);     // opens the Jiggler settings
}


// ---------- jiggler settings ----------

// A selectable pill: accent ring and tint when selected, grey when not; faded when disabled.
static void js_pill_f(int x, int y, int w, const char *label, int sel, int enabled, const aa_font_t *f) {
    uint16_t ring = sel && enabled ? C_PC : C_SURF2, bg = sel && enabled ? C_PC_TINT : C_SURF2;
    uint16_t fg = !enabled ? C_FAINT : sel ? C_PC : C_DIM;
    pill(x, y, w, JS_CTRL_H, ring);
    if (ring != bg) pill(x + 1, y + 1, w - 2, JS_CTRL_H - 2, bg);
    text_c(x + w / 2, y + JS_CTRL_H / 2, label, f, fg, f == &font_caps);
}

static void js_pill(int x, int y, int w, const char *label, int sel, int enabled) {
    js_pill_f(x, y, w, label, sel, enabled, &font_caps);
}

// "-  12 s  +" on row i; the buttons fade at the ends of the 0-60 s range.
static void js_stepper(int i, int value, int enabled) {
    int y = JS_ROW_Y(i) - JS_CTRL_H / 2;
    char v[12];
    snprintf(v, sizeof v, "%d s", value);
    js_pill_f(JS_MINUS_X, y, JS_STEP_W, "-", 0, enabled && value > 0, &font_label);
    js_pill_f(JS_PLUS_X, y, JS_STEP_W, "+", 0, enabled && value < JM_MAX_S, &font_label);
    text_c((JS_MINUS_X + JS_STEP_W + JS_PLUS_X) / 2, JS_ROW_Y(i), v, &font_label, enabled ? C_TEXT : C_FAINT, 0);
}

static void draw_jigset(const ui_state_t *s) {
    int menu = s->jig_menu_on != 0, f15 = s->jig_key != 0;
    text_c(LCD_W / 2, TITLE_Y, "Jiggler menu", &font_title, C_TEXT, 0);
    icon_close(JS_CLOSE_CX, JS_CLOSE_CY, 18.f, C_TEXT);
    const char *labels[4] = {"Context menu", "Key", "Menu open", "Pause"};
    for (int i = 0; i < 4; i++)
        gfx_text_aa(JS_LABEL_X, gfx_text_aa_ytop(&font_label, JS_ROW_Y(i)), labels[i], &font_label,
                    i == 2 && !menu ? C_FAINT : C_TEXT, 0);
    int cy = JS_ROW_Y(0) - JS_CTRL_H / 2;
    js_pill(JS_TOGGLE_X, cy, JS_TOGGLE_W, menu ? "ON" : "OFF", menu, 1);
    cy = JS_ROW_Y(1) - JS_CTRL_H / 2;
    js_pill(JS_SEG_ESC_X, cy, JS_SEG_W, "ESC", !f15, 1);
    js_pill(JS_SEG_F15_X, cy, JS_SEG_W, "F15", f15, 1);
    js_stepper(2, (int)s->jig_open_s, menu);
    js_stepper(3, (int)s->jig_pause_s, 1);
    const char *hint = menu ? (f15 ? "Right-click, wait, F15, pause" : "Right-click, wait, Esc, pause")
                            : (f15 ? "F15, then pause" : "Esc, then pause");
    text_c(LCD_W / 2, JS_HINT_Y, msg_or(s, hint), &font_body, C_DIM, 0);
}

// ---------- the page ----------

static void page_dots(int screen) {
    for (int i = 0; i < SCR_COUNT; i++)
        gfx_disc(LCD_W / 2 + (i * 2 - (SCR_COUNT - 1)) * 7, 268, 3.f, i == screen ? COL_TEXT : RGB(60, 64, 76));
}

// Screen box around the dot at box position (bx, by), wide enough for its anti-aliased edge.
rect_t ui_dot_rect(float bx, float by) {
    float x, y;
    jig_dot_screen(bx, by, &x, &y);
    int r = (int)JIG_DOT + 2;
    rect_t b = {(int)x - r, (int)y - r, 2 * r + 2, 2 * r + 2};
    return b;
}

void ui_draw_page(const ui_state_t *s) {
    gfx_fill(C_BG);
    if (s->screen == SCR_WATCH) draw_watch(s);
    else if (s->screen == SCR_CLIP) draw_clip(s);
    else if (s->sub == UI_SUB_JIGSET) draw_jigset(s);
    else draw_jig(s);
    frame_usb(s);      // last: over the content (the watch face fills the screen)
    if (s->sub) gfx_disc(LCD_W / 2, 268, 3.f, COL_TEXT);   // a panel: one dot, like the Bluetooth page
    else page_dots(s->screen);
}
