#include <math.h>
#include <stdio.h>
#include <string.h>
#include "pico/stdlib.h"
#include "pico/flash.h"
#include "app.h"
#include "board.h"
#include "gfx.h"
#include "lcd.h"
#include "ui.h"

#define CX (LCD_W / 2.f)
#define CY (LCD_H / 2.f)
#define R  116.f
#define DEG2RAD 0.017453292f

#define COL_BG     RGB(0, 0, 0)
#define COL_DIAL   RGB(18, 24, 38)
#define COL_RIM    RGB(90, 100, 120)
#define COL_MARK   RGB(200, 205, 215)
#define COL_HAND   RGB(240, 240, 240)
#define COL_SECOND RGB(255, 70, 40)
#define COL_TEXT   RGB(230, 232, 238)
#define COL_DIM    RGB(120, 128, 145)
#define COL_PANEL  RGB(24, 30, 44)
#define COL_COPY   RGB(40, 110, 220)
#define COL_PASTE  RGB(30, 160, 90)
#define COL_STOP   RGB(210, 60, 50)
#define COL_OK     RGB(60, 200, 110)

// ---------- watch ----------

static void polar(float cx, float cy, float deg, float len, float *x, float *y) {
    float a = deg * DEG2RAD;
    *x = cx + sinf(a) * len;
    *y = cy - cosf(a) * len;
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

static void draw_watch(int t) {
    int h = t / 3600, m = (t / 60) % 60, s = t % 60;
    gfx_disc(CX, CY, R, COL_DIAL);
    gfx_ring(CX, CY, R - 1.5f, 3.f, COL_RIM);

    for (int i = 0; i < 60; i++) {
        float x0, y0, x1, y1;
        int hour = (i % 5) == 0;
        polar(CX, CY, i * 6.f, hour ? R - 22.f : R - 12.f, &x0, &y0);
        polar(CX, CY, i * 6.f, R - 7.f, &x1, &y1);
        gfx_line(x0, y0, x1, y1, hour ? ((i % 15) == 0 ? 6.f : 4.f) : 1.5f, COL_MARK);
    }
    if (app.jig_on) gfx_text_centered((int)CX, 190, "JIGGLING", &Font12, COL_OK);
    if (app.helper) gfx_text_centered((int)CX, 78, "PC", &Font12, COL_DIM);
    draw_mute_icon(app.muted);

    hand(((h % 12) + m / 60.f + s / 3600.f) * 30.f, 12.f, 58.f, 8.f, COL_HAND);
    hand((m + s / 60.f) * 6.f, 14.f, 90.f, 5.f, COL_HAND);
    hand(s * 6.f, 22.f, 100.f, 2.f, COL_SECOND);
    gfx_disc(CX, CY, 6.f, COL_SECOND);
    gfx_disc(CX, CY, 2.f, COL_DIAL);
}

// ---------- clipboard ----------

static void button(int x, const char *label, uint16_t col) {
    gfx_rrect(x, BTN_Y, BTN_W, BTN_H, 12.f, col);
    gfx_text_centered(x + BTN_W / 2, BTN_Y + (BTN_H - 24) / 2, label, &Font24, COL_TEXT);
}

static void draw_clip(void) {
    enum { BOX_X = 12, BOX_Y = 40, BOX_W = 216, BOX_H = 112, COLS = 29, ROWS = 8 };
    char info[40], msg[40];

    gfx_text_centered(LCD_W / 2, 14, "Clipboard", &Font16, COL_TEXT);
    gfx_rrect(BOX_X, BOX_Y, BOX_W, BOX_H, 8.f, COL_PANEL);

    mutex_enter_blocking(&clip_mtx);
    int len = app.clip_len;
    if (len == 0) {
        gfx_text_centered(LCD_W / 2, BOX_Y + 36, "Select text on PC,", &Font12, COL_DIM);
        gfx_text_centered(LCD_W / 2, BOX_Y + 52, "then tap COPY", &Font12, COL_DIM);
    } else {
        int row = 0, col = 0;
        for (int i = 0; i < len && row < ROWS; i++) {
            char c = app.clip[i];
            if (c == '\r') continue;
            if (c == '\n') { row++; col = 0; continue; }
            if (c == '\t') c = ' ';
            if (col == COLS) { row++; col = 0; if (row >= ROWS) break; }
            gfx_char(BOX_X + 6 + col * 7, BOX_Y + 8 + row * 12, c, &Font12, COL_TEXT);
            col++;
        }
    }
    snprintf(info, sizeof info, len ? "%d chars  (%s)" : "empty", len, app.clip_src);
    bool show_msg = (int32_t)(app.msg_until_ms - now_ms()) > 0;
    if (show_msg) strcpy(msg, app.msg);
    mutex_exit(&clip_mtx);

    if (app.clip_state == CLIP_PASTING && len) {
        int w = (BOX_W - 16) * app.paste_pos / len;
        gfx_rect(BOX_X + 8, BOX_Y + BOX_H + 6, BOX_W - 16, 4, COL_PANEL);
        gfx_rect(BOX_X + 8, BOX_Y + BOX_H + 6, w, 4, COL_PASTE);
    } else {
        gfx_text_centered(LCD_W / 2, BOX_Y + BOX_H + 6, info, &Font12, COL_DIM);
    }

    button(BTN_COPY_X, app.clip_state == CLIP_COPYING ? "..." : "COPY", COL_COPY);
    if (app.clip_state == CLIP_PASTING) button(BTN_PASTE_X, "STOP", COL_STOP);
    else button(BTN_PASTE_X, "PASTE", len ? COL_PASTE : COL_PANEL);

    if (show_msg) gfx_text_centered(LCD_W / 2, 246, msg, &Font12, COL_TEXT);
    else gfx_text_centered(LCD_W / 2, 246, app.helper ? "PC helper connected" : "PC helper not running",
                           &Font12, app.helper ? COL_OK : COL_DIM);
}

// ---------- jiggler ----------

static void draw_jig(void) {
    char s[40];
    bool on = app.jig_on;
    uint16_t accent = on ? COL_OK : COL_DIM;

    gfx_text_centered(LCD_W / 2, 14, "Jiggler", &Font16, COL_TEXT);
    gfx_disc(JIG_CX, JIG_CY, JIG_R - 4.f, COL_PANEL);
    gfx_ring(JIG_CX, JIG_CY, JIG_R, 4.f, accent);
    if (on) {
        // Dot rides the ring at the pointer's current angle, nudged by radius.
        float rr = JIG_R - 14.f + (app.jig_radius - 60.f) * 0.4f;
        float x = JIG_CX + cosf(app.jig_angle) * rr, y = JIG_CY + sinf(app.jig_angle) * rr;
        gfx_disc(x, y, 7.f, COL_TEXT);
    }
    gfx_text_centered(JIG_CX, JIG_CY - 12, on ? "ON" : "OFF", &Font24, accent);

    const char *status;
    if (!on) status = "Tap circle to start";
    else if (app.jig_paused) status = "Paused for paste";
    else if (app.jig_phase == JIG_CIRCLE) {
        int secs = (int)((int32_t)(app.jig_next_menu_ms - now_ms()) / 1000);
        snprintf(s, sizeof s, "Menu in %ds", secs < 0 ? 0 : secs);
        status = s;
    } else if (app.jig_phase == JIG_MENU_OPEN || app.jig_phase == JIG_CLICK_DOWN) status = "Right-click menu";
    else if (app.jig_phase == JIG_ESC_DOWN) status = "ESC";
    else status = "Pausing";
    gfx_text_centered(LCD_W / 2, 206, status, &Font16, COL_TEXT);

    if (on) {
        uint32_t up = (now_ms() - app.jig_started_ms) / 1000;
        char st[40];
        snprintf(st, sizeof st, "%lu menus  %02lu:%02lu:%02lu", (unsigned long)app.jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
        gfx_text_centered(LCD_W / 2, 228, st, &Font12, COL_DIM);
    }
}

// ---------- frame loop ----------

static void page_dots(int screen) {
    for (int i = 0; i < SCR_COUNT; i++)
        gfx_disc(LCD_W / 2 + (i - 1) * 14, 268, 3.f, i == screen ? COL_TEXT : RGB(60, 64, 76));
}

void ui_core1_main(void) {
    // Lets core0 park this core while it writes settings to flash.
    flash_safe_execute_core_init();

    uint32_t drawn_seq = ~0u, last_frame = 0;
    int last_watch_t = -1;
    bool first = true;

    for (;;) {
        int screen = app.screen;
        uint32_t seq = app.redraw_seq;
        // Watch redraws on request (each second); other screens animate.
        uint32_t period = (screen == SCR_JIG && app.jig_on) || app.clip_state != CLIP_IDLE ? 50 : 250;
        bool due = seq != drawn_seq || (screen != SCR_WATCH && now_ms() - last_frame >= period);
        if (!due) { sleep_ms(2); continue; }
        drawn_seq = seq;
        last_frame = now_ms();

        int t = app.time_s;
        gfx_fill(COL_BG);
        if (screen == SCR_WATCH) draw_watch(t);
        else if (screen == SCR_CLIP) draw_clip();
        else draw_jig();
        page_dots(screen);

        lcd_push_frame(fb);
        lcd_wait();
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
