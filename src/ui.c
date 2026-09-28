#include <math.h>
#include <stdio.h>
#include <string.h>
#include "pico/stdlib.h"
#include "pico/flash.h"
#include "app.h"
#include "board.h"
#include "gfx.h"
#include "icons.h"
#include "lcd.h"
#include "ui.h"

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

// Touch Deck design language (shared with esp32c3/src/ui.cpp). This board
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

typedef struct { int x, y, w, h; } rect_t;

static rect_t rect_union(rect_t a, rect_t b) {
    int x0 = a.x < b.x ? a.x : b.x, y0 = a.y < b.y ? a.y : b.y;
    int x1 = a.x + a.w > b.x + b.w ? a.x + a.w : b.x + b.w;
    int y1 = a.y + a.h > b.y + b.h ? a.y + a.h : b.y + b.h;
    return (rect_t){x0, y0, x1 - x0, y1 - y0};
}

// The three hands at time t (seconds of day): one definition for drawing them
// and for working out what a per-second partial redraw has to cover.
typedef struct { float deg, tail, len, thick; uint16_t col; } hand_t;

static void watch_hands(int t, hand_t out[3]) {
    int h = t / 3600, m = (t / 60) % 60, s = t % 60;
    out[0] = (hand_t){((h % 12) + m / 60.f + s / 3600.f) * 30.f, 12.f, 58.f, 8.f, COL_HAND};
    out[1] = (hand_t){(m + s / 60.f) * 6.f, 14.f, 90.f, 5.f, COL_HAND};
    out[2] = (hand_t){s * 6.f, 22.f, 100.f, 2.f, COL_SECOND};
}

// Pixels the hands (and the centre cap) can touch at time t.
static rect_t hands_rect(int t) {
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
    return (rect_t){(int)floorf(x0), (int)floorf(y0), (int)ceilf(x1 - x0) + 1, (int)ceilf(y1 - y0) + 1};
}

// The stopwatch's black box below the centre.
static rect_t stopwatch_rect(void) {
    int tw = gfx_text_aa_width("88:88:88", &font_timer, 0), bw = tw + 16, bh = font_timer.cap_h + 14;
    int cy = (int)CY + 50;
    return (rect_t){(int)CX - bw / 2, cy - bh / 2, bw, bh};
}

static void hand(float deg, float tail, float len, float thick, uint16_t col) {
    float x0, y0, x1, y1;
    polar(CX, CY, deg + 180.f, tail, &x0, &y0);
    polar(CX, CY, deg, len, &x1, &y1);
    gfx_line(x0, y0, x1, y1, thick, col);
}

// Speaker glyph: body + flared cone, then sound waves or a red X.
static void draw_mute_icon(bool muted) {
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

static void draw_watch(int t) {
    static bool ticks_ready;
    if (!ticks_ready) { tick_reach_init(); ticks_ready = true; }

    gfx_rrect(FACE_M, FACE_M, LCD_W - 2 * FACE_M, LCD_H - 2 * FACE_M, FACE_R, COL_DIAL);
    gfx_rrect_ring(FACE_M, FACE_M, LCD_W - 2 * FACE_M, LCD_H - 2 * FACE_M, FACE_R, 3.f, COL_RIM);

    for (int i = 0; i < 60; i++) {
        float x0, y0, x1, y1;
        int hour = (i % 5) == 0;
        polar(CX, CY, i * 6.f, tick_reach[i] - (hour ? 15.f : 6.f), &x0, &y0);
        polar(CX, CY, i * 6.f, tick_reach[i], &x1, &y1);
        gfx_line(x0, y0, x1, y1, hour ? ((i % 15) == 0 ? 6.f : 4.f) : 1.5f, COL_MARK);
    }
    if (app.helper)
        gfx_text_aa_centered((int)CX, gfx_text_aa_ytop(&font_caps, 84), "PC", &font_caps, COL_DIM, 1);
    draw_mute_icon(app.muted);

    // Stopwatch (BOOT button): black box below the centre, over the dial but
    // under the hands. 7-segment digits at twice the chip label's size.
    {
        char st[12];
        int ts = app.timer_s;
        snprintf(st, sizeof st, "%02d:%02d:%02d", (ts / 3600) % 100, ts / 60 % 60, ts % 60);
        rect_t box = stopwatch_rect();
        int bh = box.h, cy = box.y + box.h / 2;
        gfx_rrect(box.x, box.y, box.w, box.h, 6.f, RGB(0, 0, 0));
        gfx_text_aa_centered((int)CX, gfx_text_aa_ytop(&font_timer, cy), st, &font_timer, RGB(255, 255, 255), 0);
        if (app.jig_on)       // tucked just under the stopwatch
            gfx_text_aa_centered((int)CX, gfx_text_aa_ytop(&font_caps, cy + bh / 2 + 8), "JIGGLING",
                                 &font_caps, COL_OK, 1);
    }

    hand_t hs[3];
    watch_hands(t, hs);
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

// Transient message (app_message) if one is showing, else the given text.
static const char *msg_or(const char *normal, char *buf, int n) {
    mutex_enter_blocking(&clip_mtx);
    bool show = (int32_t)(app.msg_until_ms - now_ms()) > 0;
    if (show) { strncpy(buf, app.msg, n - 1); buf[n - 1] = 0; }
    mutex_exit(&clip_mtx);
    return show ? buf : normal;
}

// Edge ring + top chip on every screen: output goes to USB, dot = enumerated.
static void frame_usb(void) {
    // Corner radius matched on the device to the panel's rounded glass (40 clipped, 46 was loose).
    gfx_rrect_ring(0, 0, LCD_W, LCD_H, 44.f, 3.f, C_PC);
    const char *label = "USB";
    int tw = gfx_text_aa_width(label, &font_caps, 1), w = 8 + 10 + 4 + tw + 5 + 5 + 8, x = 120 - w / 2, y = 5;
    pill(x, y, w, 18, C_PC_TINT);
    icon_monitor(x + 13.f, y + 9.f, 10.f, C_PC);
    text_at(x + 22, y + 9, label, &font_caps, C_PC, 1);
    gfx_disc(x + w - 10.5f, y + 9.f, 2.6f, app.usb_mounted ? C_OK : C_BAD);
}

// ---------- clipboard ----------

static void draw_clip(void) {
    enum { CX0 = 20, CY0 = 64, CW = 200, CH = 110, ROWS = 8, PITCH = 12 };
    const int cols = (CW - 16) / font_mono.glyphs['M' - ' '].adv;
    char info[40], buf[40];
    text_c(LCD_W / 2, 48, "Clipboard", &font_title, C_TEXT, 0);

    mutex_enter_blocking(&clip_mtx);
    int len = app.clip_len;
    gfx_rrect(CX0, CY0, CW, CH, 12.f, len ? C_SURF : C_INNER);
    if (len == 0) {
        text_c(LCD_W / 2, CY0 + 44, "Select text on PC,", &font_body, C_DIM, 0);
        text_c(LCD_W / 2, CY0 + 60, "then tap Copy", &font_body, C_DIM, 0);
    } else {
        // Wrap into rows of monospaced text, drawn a row at a time.
        char line[40];
        int row = 0, col = 0;
        for (int i = 0; i <= len && row < ROWS; i++) {
            char c = i < len ? app.clip[i] : '\n';
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
    snprintf(info, sizeof info, len ? "%d chars from %s" : "Empty", len, app.clip_src);
    mutex_exit(&clip_mtx);

    bool pasting = app.clip_state == CLIP_PASTING;
    if (pasting && len) {
        gfx_rrect(40, 186, 160, 4, 2.f, C_SURF2);
        gfx_rrect(40, 186, 160 * app.paste_pos / len + 1, 4, 2.f, C_PC);
    } else {
        text_c(LCD_W / 2, 188, msg_or(info, buf, sizeof buf), &font_body, C_DIM, 0);
    }

    button(BTN_COPY_X, BTN_Y, BTN_W, BTN_H, C_SURF2, C_TEXT,
           app.clip_state == CLIP_COPYING ? "..." : "Copy", icon_copy, NULL);
    if (pasting) button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, C_BAD, C_BG, "Stop", NULL, NULL);
    else button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, len ? C_PC : C_SURF2, len ? C_BG : C_FAINT,
                "Paste", NULL, icon_arrow_right);
}

// ---------- jiggler ----------

static void draw_jig(void) {
    char s[40], buf[40];
    bool on = app.jig_on;
    text_c(LCD_W / 2, 48, "Jiggler", &font_title, C_TEXT, 0);
    gfx_ring(JIG_CX, JIG_CY, JIG_R, 2.f, on ? C_PC : C_SURF2);
    gfx_disc(JIG_CX, JIG_CY, 49.f, C_INNER);
    if (on || app.anim_demo) {
        // The dot's orbit grows with the BOOT-button scale (1x inside, 2x at the ring).
        float orbit = 40.f + 15.f * (JIG_SCALES[app.jig_scale_idx] - 1.f);
        gfx_disc(JIG_CX + cosf(app.jig_angle) * orbit, JIG_CY + sinf(app.jig_angle) * orbit, 6.f, C_PC);
    }
    text_c(JIG_CX, JIG_CY - 5, on ? "ON" : "OFF", &font_big, on ? C_PC : C_DIM, 0);
    text_c(JIG_CX, JIG_CY + 17, on ? "TAP TO STOP" : "TAP TO START", &font_caps, C_DIM, 1);
    {
        char sc[8];
        snprintf(sc, sizeof sc, "%.1fX", (double)JIG_SCALES[app.jig_scale_idx]);
        int w = gfx_text_aa_width(sc, &font_caps, 1) + 12;
        pill(JIG_CX - w / 2, JIG_CY + 29, w, 15, C_SURF2);
        text_c(JIG_CX, JIG_CY + 37, sc, &font_caps, C_PC, 1);
    }

    const char *status = "Tap to start";
    uint16_t scol = C_TEXT;
    if (on && !app.usb_mounted) { status = "Plug into USB"; scol = C_BAD_TXT; }
    else if (on && app.jig_paused) status = "Paused: pasting";
    else if (on && app.jig_phase == JIG_CIRCLE) {
        int secs = (int)((int32_t)(app.jig_next_menu_ms - now_ms()) / 1000);
        snprintf(s, sizeof s, "Next menu in %ds", secs < 0 ? 0 : secs);
        status = s;
    } else if (on && (app.jig_phase == JIG_CLICK_DOWN || app.jig_phase == JIG_MENU_OPEN)) status = "Right-click menu";
    else if (on && app.jig_phase == JIG_ESC_DOWN) status = "Esc";
    else if (on) status = "Pausing";
    text_c(LCD_W / 2, 220, status, &font_label, scol, 0);

    char stats[40];
    if (on) {
        uint32_t up = (now_ms() - app.jig_started_ms) / 1000;
        snprintf(stats, sizeof stats, "%lu menus %02lu:%02lu:%02lu", (unsigned long)app.jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
    } else {
        strcpy(stats, "Menu every 45-150s");
    }
    text_c(LCD_W / 2, 238, msg_or(stats, buf, sizeof buf), &font_body, C_DIM, 0);
}

// ---------- frame loop ----------

static void page_dots(int screen) {
    for (int i = 0; i < SCR_COUNT; i++)
        gfx_disc(LCD_W / 2 + (i - 1) * 14, 268, 3.f, i == screen ? COL_TEXT : RGB(60, 64, 76));
}

// Regions that change on an animation frame (everything else is redrawn only
// when core0 bumps redraw_seq): the jiggler's orbit + pill and status lines,
// or the clipboard's progress line while pasting.
static const rect_t JIG_ANIM[] = {
    {JIG_CX - JIG_R - 3, JIG_CY - JIG_R - 3, 2 * (JIG_R + 3), 2 * (JIG_R + 3)},
    {0, 206, LCD_W, 44},
};
static const rect_t CLIP_ANIM[] = {{0, 178, LCD_W, 20}};

static void draw_page(int screen, int t) {
    gfx_fill(C_BG);
    if (screen == SCR_WATCH) draw_watch(t);
    else if (screen == SCR_CLIP) draw_clip();
    else draw_jig();
    frame_usb();      // last: over the content (the watch face fills the screen)
    page_dots(screen);
}

static bool msg_showing(void) { return (int32_t)(app.msg_until_ms - now_ms()) > 0; }

void ui_core1_main(void) {
    // Lets core0 park this core while it writes settings to flash.
    flash_safe_execute_core_init();

    uint32_t drawn_seq = ~0u, last_frame = 0;
    int last_watch_t = -1, drawn_screen = -1;
    uint32_t drawn_wtick = 0;
    rect_t prev_hands = {0, 0, LCD_W, LCD_H};   // where the hands were on the last watch frame
    bool first = true, msg_shown = false;

    for (;;) {
        int screen = app.screen;
        uint32_t seq = app.redraw_seq;
        bool anim_jig = screen == SCR_JIG && (app.jig_on || app.anim_demo);
        bool anim_clip = screen == SCR_CLIP && app.clip_state == CLIP_PASTING;
        bool msg = msg_showing();
        bool full = seq != drawn_seq || screen != drawn_screen || msg != msg_shown;
        bool partial = !full && (anim_jig || anim_clip) && now_ms() - last_frame >= 50;
        uint32_t wtick = app.watch_tick;
        bool watch_partial = !full && screen == SCR_WATCH && wtick != drawn_wtick;
        if (!full && !partial && !watch_partial) { sleep_ms(2); continue; }
        drawn_wtick = wtick;
        drawn_seq = seq;
        drawn_screen = screen;
        msg_shown = msg;
        last_frame = now_ms();
        if (app.anim_demo && anim_jig) app.jig_angle += 0.12f;   // ANIM 1: spin the dot, no HID

        int t = app.time_s;
        uint64_t t_draw = time_us_64(), t_push;
        if (full) {
            draw_page(screen, t);
            t_push = time_us_64();
            lcd_push_frame(fb);
            lcd_wait();
            if (screen == SCR_WATCH) prev_hands = hands_rect(t);
        } else if (watch_partial) {
            // New second: repaint where the hands were and where they are now
            // (dial and ticks underneath come back via the clip), plus the stopwatch.
            rect_t now_hands = hands_rect(t);
            rect_t r = rect_union(rect_union(prev_hands, now_hands), stopwatch_rect());
            gfx_set_clip(r.x, r.y, r.w, r.h);
            draw_page(screen, t);
            gfx_clip_reset();
            t_push = time_us_64();
            if (r.x < 0) { r.w += r.x; r.x = 0; }
            if (r.y < 0) { r.h += r.y; r.y = 0; }
            if (r.x + r.w > LCD_W) r.w = LCD_W - r.x;
            if (r.y + r.h > LCD_H) r.h = LCD_H - r.y;
            lcd_push_rect(fb, r.x, r.y, r.w, r.h);
            prev_hands = now_hands;
        } else {
            const rect_t *r = anim_jig ? JIG_ANIM : CLIP_ANIM;
            int n = anim_jig ? 2 : 1;
            for (int i = 0; i < n; i++) {           // draw each region clipped, then send just it
                gfx_set_clip(r[i].x, r[i].y, r[i].w, r[i].h);
                draw_page(screen, t);
            }
            gfx_clip_reset();
            t_push = time_us_64();
            for (int i = 0; i < n; i++) lcd_push_rect(fb, r[i].x, r[i].y, r[i].w, r[i].h);
        }
        // Frame timing for DBG: exponential averages (1/8) and the worst draw seen.
        uint32_t draw_us = (uint32_t)(t_push - t_draw), push_us = (uint32_t)(time_us_64() - t_push);
        app.perf_draw_us += ((int32_t)draw_us - (int32_t)app.perf_draw_us) / 8;
        app.perf_push_us += ((int32_t)push_us - (int32_t)app.perf_push_us) / 8;
        if (draw_us > app.perf_draw_max_us) app.perf_draw_max_us = draw_us;
        app.frames++;
        if (first) { lcd_set_backlight(80); first = false; }

        // Tick only when the second hand actually advanced on screen.
        if (screen == SCR_WATCH) {
            if (last_watch_t >= 0 && t != last_watch_t) app.tick_pending = true;
            last_watch_t = t;
        } else {
            last_watch_t = -1;
        }
    }
}
