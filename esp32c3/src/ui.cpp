// Round-screen UI, built to the approved mockup
// (https://claude.ai/artifact/QbZAqAnnHaYKeZfYejQRWz). One accent colour
// carries the output mode everywhere: amber = PC, blue = Bluetooth.
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "app.h"
#include "ble_hid.h"
#include "board.h"
#include "display.h"
#include "gfx.h"
#include "icons.h"
#include "output.h"
#include "ui.h"

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

static uint16_t accent() { return mode_get() == MODE_BT ? C_BT : C_PC; }

// Centred text with its capitals vertically centred on cy.
static void text_c(int cy, const char *s, const aa_font_t *f, uint16_t col, int spacing = 0) {
    gfx_text_aa_centered(LCD_W / 2, gfx_text_aa_ytop(f, cy), s, f, col, spacing);
}

static void text_at(int x, int cy, const char *s, const aa_font_t *f, uint16_t col, int spacing = 0) {
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
    text_at(cx, cy, label, f, fg);
    cx += tw + gap;
    if (trail) trail(cx + iw / 2.f, cy, iw, fg);
}

// Transient message (app_message) if one is showing, else the given text.
static const char *msg_or(const char *normal, char *buf, int n) {
    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    bool show = (int32_t)(app.msg_until_ms - now_ms()) > 0;
    if (show) { strncpy(buf, app.msg, n - 1); buf[n - 1] = 0; }
    xSemaphoreGive(clip_mtx);
    return show ? buf : normal;
}

// Edge ring + top chip: where output goes, and whether that link is live.
static void frame() {
    gfx_fill(C_BG);
    gfx_ring(120.f, 120.f, 118.5f, 3.f, accent());
    bool bt = mode_get() == MODE_BT;
    const char *label = bt ? "BLUETOOTH" : "PC";
    int tw = gfx_text_aa_width(label, &font_caps, 1), w = 8 + 10 + 4 + tw + 5 + 5 + 8, x = 120 - w / 2, y = 16;
    pill(x, y, w, 18, bt ? C_BT_TINT : C_PC_TINT);
    (bt ? icon_bt : icon_monitor)(x + 13.f, y + 9.f, 10.f, accent());
    text_at(x + 22, y + 9, label, &font_caps, accent(), 1);
    gfx_disc(x + w - 10.5f, y + 9.f, 2.6f, out_ready() ? C_OK : C_BAD);
}

static void dots(int count, int active) {
    for (int i = 0; i < count; i++)
        gfx_disc(120.f + (i * 2 - (count - 1)) * 6.f, 229.f, 2.6f, i == active ? C_TEXT : C_DOT_OFF);
}

static void draw_clip() {
    enum { CX0 = 32, CY0 = 58, CW = 176, CH = 86, ROWS = 6, PITCH = 12 };
    const int cols = (CW - 16) / font_mono.glyphs['M' - ' '].adv;
    char info[40], buf[40];
    text_c(46, "Clipboard", &font_title, C_TEXT);

    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    int len = app.clip_len;
    gfx_rrect(CX0, CY0, CW, CH, 12.f, len ? C_SURF : C_INNER);
    if (len == 0) {
        text_c(CY0 + 34, "Select text on PC,", &font_body, C_DIM);
        text_c(CY0 + 50, "then tap Copy", &font_body, C_DIM);
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
    xSemaphoreGive(clip_mtx);

    bool pasting = app.clip_state == CLIP_PASTING;
    if (pasting && len) {
        gfx_rrect(48, 152, 144, 4, 2.f, C_SURF2);
        gfx_rrect(48, 152, 144 * app.paste_pos / len + 1, 4, 2.f, accent());
    } else {
        text_c(155, msg_or(info, buf, sizeof buf), &font_body, C_DIM);
    }

    button(BTN_COPY_X, BTN_Y, BTN_W, BTN_H, C_SURF2, C_TEXT,
           app.clip_state == CLIP_COPYING ? "..." : "Copy", icon_copy, nullptr);
    if (pasting) button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, C_BAD, C_BG, "Stop", nullptr, nullptr);
    else button(BTN_PASTE_X, BTN_Y, BTN_W, BTN_H, len ? accent() : C_SURF2, len ? C_BG : C_FAINT,
                "Paste", nullptr, icon_arrow_right);
}

static void draw_jig() {
    char s[40], buf[40];
    bool on = app.jig_on;
    text_c(46, "Jiggler", &font_title, C_TEXT);
    gfx_ring(JIG_CX, JIG_CY, JIG_R, 2.f, on ? accent() : C_SURF2);
    gfx_disc(JIG_CX, JIG_CY, 44.f, C_INNER);
    if (on)
        gfx_disc(JIG_CX + cosf(app.jig_angle) * 50.f, JIG_CY + sinf(app.jig_angle) * 50.f, 6.f, accent());
    text_c(JIG_CY - 5, on ? "ON" : "OFF", &font_big, on ? accent() : C_DIM);
    text_c(JIG_CY + 17, on ? "TAP TO STOP" : "TAP TO START", &font_caps, C_DIM, 1);

    const char *status = "Tap to start";
    uint16_t scol = C_TEXT;
    if (on && !out_ready()) { status = out_down_reason(); scol = C_BAD_TXT; }
    else if (on && app.jig_paused) status = "Paused: pasting";
    else if (on && app.jig_phase == JIG_CIRCLE) {
        int secs = (int)((int32_t)(app.jig_next_menu_ms - now_ms()) / 1000);
        snprintf(s, sizeof s, "Next menu in %ds", secs < 0 ? 0 : secs);
        status = s;
    } else if (on && (app.jig_phase == JIG_CLICK_DOWN || app.jig_phase == JIG_MENU_OPEN)) status = "Right-click menu";
    else if (on && app.jig_phase == JIG_ESC_DOWN) status = "Esc";
    else if (on) status = "Pausing";
    text_c(190, status, &font_label, scol);

    char stats[40];
    if (on) {
        uint32_t up = (now_ms() - app.jig_started_ms) / 1000;
        snprintf(stats, sizeof stats, "%lu menus %02lu:%02lu:%02lu", (unsigned long)app.jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
    } else {
        strcpy(stats, "Menu every 45-150s");
    }
    text_c(206, msg_or(stats, buf, sizeof buf), &font_body, C_DIM);
}

static const char *bt_row_status(uint16_t *col) {
    *col = C_DIM;
    switch (ble_state()) {
    case BT_CONNECTED: *col = C_OK; return "Connected";
    case BT_PAIRING: return "Pairing...";
    case BT_WAITING: return "Paired";
    case BT_OFF: return "Paired, off";
    default: return "Not paired";
    }
}

static void draw_settings() {
    char buf[40];
    int tw = gfx_text_aa_width("Settings", &font_title, 0), x0 = 120 - (15 + 6 + tw) / 2;
    icon_cog(x0 + 7.5f, 46.f, 15.f, C_DIM);
    text_at(x0 + 21, 46, "Settings", &font_title, C_TEXT);
    text_c(65, "OUTPUT", &font_caps, C_DIM, 2);

    bool avail = mode_bt_available(), bt = mode_get() == MODE_BT;
    pill(38, 74, 164, 40, C_SURF);
    button(SEG_BT_X, SEG_Y, SEG_W, SEG_H, bt ? C_BT : C_SURF, bt ? C_BG : (avail ? C_DIM : C_FAINT),
           "Bluetooth", avail ? icon_bt : icon_lock, nullptr);
    button(SEG_PC_X, SEG_Y, SEG_W, SEG_H, bt ? C_SURF : C_PC, bt ? C_DIM : C_BG,
           "PC", icon_monitor, nullptr);
    const char *cap = !avail ? "Pair Bluetooth to switch" : (bt ? "Sends to Bluetooth host" : "Sends to this PC");
    text_c(126, msg_or(cap, buf, sizeof buf), &font_body, C_DIM);

    uint16_t scol;
    const char *st = bt_row_status(&scol);
    gfx_rrect(ROW_X, ROW_Y, ROW_W, ROW_H, 14.f, C_SURF);
    gfx_disc(55.f, 162.f, 13.f, RGB(28, 42, 67));
    icon_bt(55.f, 162.f, 13.f, C_BT);
    text_at(75, 154, "Bluetooth", &font_label, C_TEXT);
    text_at(75, 170, st, &font_body, scol);
    icon_chevron_right(193.f, 162.f, 11.f, C_DIM);
}

static void draw_bt() {
    char s[40], buf[40];
    bt_state_t st = ble_state();
    gfx_disc(BACK_CX, BACK_CY, 14.f, C_SURF);
    icon_chevron_left(BACK_CX - 1.f, BACK_CY, 12.f, C_TEXT);
    text_c(49, "Bluetooth", &font_title, C_TEXT);

    if (st == BT_UNPAIRED) {
        gfx_disc(120.f, 94.f, 26.f, C_BT_TINT);
        icon_bt(120.f, 94.f, 22.f, C_BT);
        text_c(134, "Not paired", &font_title, C_TEXT);
        text_c(152, "PC: Settings > Bluetooth >", &font_body, C_DIM);
        text_c(166, msg_or("Add device > Touch Deck", buf, sizeof buf), &font_body, C_DIM);
        button(BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H, C_BT, C_BG, "Pair", nullptr, nullptr);
    } else if (st == BT_PAIRING) {
        text_c(72, "Enter this PIN on your PC", &font_body, C_DIM);
        snprintf(s, sizeof s, "%06lu", (unsigned long)ble_passkey());
        for (int i = 0; i < 6; i++) {
            int bx = 47 + i * 24 + (i >= 3 ? 5 : 0);
            gfx_rrect(bx, 84, 21, 30, 6.f, C_SURF);
            char d[2] = {s[i], 0};
            gfx_text_aa_centered(bx + 11, gfx_text_aa_ytop(&font_pin, 99), d, &font_pin, C_TEXT, 0);
        }
        text_c(130, msg_or("Pick \"Touch Deck\" in Add device", buf, sizeof buf), &font_body, C_DIM);
        int left = ble_pair_secs_left();
        snprintf(s, sizeof s, "Expires in %d:%02d", left / 60, left % 60);
        text_c(146, s, &font_body, C_BT);
        button(BT_BTN1_X, BT_BTN1_Y, BT_BTN1_W, BT_BTN1_H, C_SURF2, C_TEXT, "Cancel", nullptr, nullptr);
    } else {
        bool conn = st == BT_CONNECTED, off = st == BT_OFF;
        uint16_t dot = conn ? C_OK : C_DIM;
        gfx_disc(120.f, 92.f, 25.f, C_SURF);
        icon_monitor(120.f, 91.f, 22.f, C_TEXT);
        gfx_disc(139.f, 110.f, 6.5f, C_BG);
        gfx_disc(139.f, 110.f, 4.5f, dot);
        text_c(127, conn ? "CONNECTED" : (off ? "DISCONNECTED" : "WAITING"), &font_caps, dot, 1);
        const char *host = ble_host_name();
        snprintf(s, sizeof s, "%.18s", host[0] ? host : "Paired PC");
        text_c(145, s, &font_title, C_TEXT);
        const char *sub = conn ? (ble_ready() ? "Keyboard + mouse" : "Connecting...")
                        : off ? "Tap Connect to resume" : "PC will reconnect";
        text_c(163, msg_or(sub, buf, sizeof buf), &font_body, C_DIM);
        if (off) button(BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_OK, C_BG, "Connect", nullptr, nullptr);
        else button(BT_BTN_L_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_SURF2, C_TEXT, "Disconnect", nullptr, nullptr);
        pill(BT_BTN_R_X, BT_BTN2_Y, BT_BTN2_W, BT_BTN2_H, C_BAD);
        pill(BT_BTN_R_X + 1, BT_BTN2_Y + 1, BT_BTN2_W - 2, BT_BTN2_H - 2, C_BG);
        gfx_text_aa_centered(BT_BTN_R_X + BT_BTN2_W / 2, gfx_text_aa_ytop(&font_button, BT_BTN2_Y + BT_BTN2_H / 2),
                             "Forget", &font_button, C_BAD_TXT, 0);
    }
}

void ui_task(void *) {
    uint32_t drawn_seq = ~0u, last_frame = 0;
    bool first = true;
    for (;;) {
        int screen = app.screen;
        bool in_bt = app.in_bt;
        uint32_t seq = app.redraw_seq;
        uint32_t period = (screen == SCR_JIG && app.jig_on) || app.clip_state != CLIP_IDLE ? 100 : 500;
        if (seq == drawn_seq && now_ms() - last_frame < period) {
            vTaskDelay(pdMS_TO_TICKS(5));
            continue;
        }
        drawn_seq = seq;
        last_frame = now_ms();

        frame();
        if (in_bt) { draw_bt(); dots(1, 0); }
        else {
            if (screen == SCR_CLIP) draw_clip();
            else if (screen == SCR_JIG) draw_jig();
            else draw_settings();
            dots(SCR_COUNT, screen);
        }
        display_push();
        display_wait();
        app.frames++;
        if (first) { display_backlight(200); first = false; }
    }
}
