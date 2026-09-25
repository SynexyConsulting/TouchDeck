#include <math.h>
#include <stdio.h>
#include <string.h>
#include "app.h"
#include "ble_hid.h"
#include "board.h"
#include "display.h"
#include "gfx.h"
#include "ui.h"

#define COL_BG    RGB(0, 0, 0)
#define COL_TEXT  RGB(230, 232, 238)
#define COL_DIM   RGB(120, 128, 145)
#define COL_PANEL RGB(24, 30, 44)
#define COL_COPY  RGB(40, 110, 220)
#define COL_PASTE RGB(30, 160, 90)
#define COL_STOP  RGB(210, 60, 50)
#define COL_OK    RGB(60, 200, 110)
#define COL_WARN  RGB(230, 170, 40)

#define COL_FORGET RGB(110, 36, 36)

static void button_at(int x, int y, int w, int h, const char *label, const sFONT *font, uint16_t col) {
    gfx_rrect(x, y, w, h, 12.f, col);
    gfx_text_centered(x + w / 2, y + (h - font->Height) / 2, label, font, COL_TEXT);
}

static void button(int x, const char *label, uint16_t col) {
    button_at(x, BTN_Y, BTN_W, BTN_H, label, &Font16, col);
}

// Top: clock (once the helper has sent the time). Bottom: link status.
static void header_footer() {
    if (app.time_s >= 0) {
        char t[8];
        snprintf(t, sizeof t, "%02d:%02d", app.time_s / 3600, app.time_s / 60 % 60);
        gfx_text_centered(LCD_W / 2, 8, t, &Font12, COL_DIM);
    }
    // "BT ok  PC ok": Bluetooth HID link and the USB helper link.
    const char *bt = ble_ready() ? "BT ok" : (ble_connected() ? "BT ..." : "BT pair");
    uint16_t btc = ble_ready() ? COL_OK : COL_WARN;
    const char *pc = app.helper ? "PC ok" : "PC -";
    uint16_t pcc = app.helper ? COL_OK : COL_DIM;
    int w_bt = (int)strlen(bt) * 7, w_pc = (int)strlen(pc) * 7, gap = 14;
    int x0 = LCD_W / 2 - (w_bt + gap + w_pc) / 2;
    gfx_text(x0, 214, bt, &Font12, btc);
    gfx_text(x0 + w_bt + gap, 214, pc, &Font12, pcc);
}

static void draw_clip() {
    enum { BOX_X = 24, BOX_Y = 46, BOX_W = 192, BOX_H = 100, COLS = 25, ROWS = 7 };
    char info[40], msg[40];

    gfx_text_centered(LCD_W / 2, 24, "Clipboard", &Font16, COL_TEXT);
    gfx_rrect(BOX_X, BOX_Y, BOX_W, BOX_H, 10.f, COL_PANEL);

    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    int len = app.clip_len;
    if (len == 0) {
        gfx_text_centered(LCD_W / 2, BOX_Y + 32, "Select text on PC,", &Font12, COL_DIM);
        gfx_text_centered(LCD_W / 2, BOX_Y + 48, "then tap COPY", &Font12, COL_DIM);
    } else {
        int row = 0, col = 0;
        for (int i = 0; i < len && row < ROWS; i++) {
            char c = app.clip[i];
            if (c == '\r') continue;
            if (c == '\n') { row++; col = 0; continue; }
            if (c == '\t') c = ' ';
            if (col == COLS) { row++; col = 0; if (row >= ROWS) break; }
            gfx_char(BOX_X + 8 + col * 7, BOX_Y + 8 + row * 12, c, &Font12, COL_TEXT);
            col++;
        }
    }
    snprintf(info, sizeof info, len ? "%d chars (%s)" : "empty", len, app.clip_src);
    bool show_msg = (int32_t)(app.msg_until_ms - now_ms()) > 0;
    if (show_msg) strcpy(msg, app.msg);
    xSemaphoreGive(clip_mtx);

    if (app.clip_state == CLIP_PASTING && len) {
        int w = (BOX_W - 24) * app.paste_pos / len;
        gfx_rect(BOX_X + 12, 152, BOX_W - 24, 4, COL_PANEL);
        gfx_rect(BOX_X + 12, 152, w, 4, COL_PASTE);
    } else {
        gfx_text_centered(LCD_W / 2, 150, show_msg ? msg : info, &Font12, show_msg ? COL_TEXT : COL_DIM);
    }

    button(BTN_COPY_X, app.clip_state == CLIP_COPYING ? "..." : "COPY", COL_COPY);
    if (app.clip_state == CLIP_PASTING) button(BTN_PASTE_X, "STOP", COL_STOP);
    else button(BTN_PASTE_X, "PASTE", len ? COL_PASTE : COL_PANEL);
}

static void draw_jig() {
    char s[40];
    bool on = app.jig_on;
    uint16_t accent = on ? COL_OK : COL_DIM;

    gfx_text_centered(LCD_W / 2, 24, "Jiggler", &Font16, COL_TEXT);
    gfx_disc(JIG_CX, JIG_CY, JIG_R - 4.f, COL_PANEL);
    gfx_ring(JIG_CX, JIG_CY, JIG_R, 4.f, accent);
    if (on) {
        float rr = JIG_R - 13.f + (app.jig_radius - 60.f) * 0.3f;
        gfx_disc(JIG_CX + cosf(app.jig_angle) * rr, JIG_CY + sinf(app.jig_angle) * rr, 6.f, COL_TEXT);
    }
    gfx_text_centered(JIG_CX, JIG_CY - 12, on ? "ON" : "OFF", &Font24, accent);

    const char *status;
    if (!on) status = "Tap to start";
    else if (!ble_ready()) status = "Waiting for BT";
    else if (app.jig_paused) status = "Paused: paste";
    else if (app.jig_phase == JIG_CIRCLE) {
        int secs = (int)((int32_t)(app.jig_next_menu_ms - now_ms()) / 1000);
        snprintf(s, sizeof s, "Menu in %ds", secs < 0 ? 0 : secs);
        status = s;
    } else if (app.jig_phase == JIG_MENU_OPEN || app.jig_phase == JIG_CLICK_DOWN) status = "Right-click";
    else if (app.jig_phase == JIG_ESC_DOWN) status = "ESC";
    else status = "Pausing";
    gfx_text_centered(LCD_W / 2, 176, status, &Font16, COL_TEXT);

    if (on) {
        uint32_t up = (now_ms() - app.jig_started_ms) / 1000;
        char st[40];
        snprintf(st, sizeof st, "%lu menus %02lu:%02lu:%02lu", (unsigned long)app.jig_menus,
                 (unsigned long)(up / 3600), (unsigned long)(up / 60 % 60), (unsigned long)(up % 60));
        gfx_text_centered(LCD_W / 2, 196, st, &Font12, COL_DIM);
    }
}

static const char *bt_summary(uint16_t *col) {
    switch (ble_state()) {
    case BT_CONNECTED: *col = COL_OK; return "Connected";
    case BT_PAIRING:   *col = COL_WARN; return "Pairing...";
    case BT_WAITING:   *col = COL_WARN; return "Waiting for PC";
    case BT_OFF:       *col = COL_DIM; return "Disconnected";
    default:           *col = COL_DIM; return "Not paired";
    }
}

// Gear: body disc + 8 teeth + hole, in the style of the iOS/Android settings icon.
static void draw_cog(float cx, float cy, uint16_t col, uint16_t hole) {
    for (int i = 0; i < 8; i++) {
        float a = i * 0.7854f;
        gfx_line(cx + cosf(a) * 28.f, cy + sinf(a) * 28.f, cx + cosf(a) * 40.f, cy + sinf(a) * 40.f, 13.f, col);
    }
    gfx_disc(cx, cy, 31.f, col);
    gfx_disc(cx, cy, 13.f, hole);
}

static void draw_settings() {
    uint16_t col;
    const char *bt = bt_summary(&col);
    gfx_text_centered(LCD_W / 2, 24, "Settings", &Font16, COL_TEXT);
    draw_cog(COG_CX, COG_CY, COL_DIM, COL_BG);
    gfx_text_centered(LCD_W / 2, 152, "Bluetooth", &Font16, COL_TEXT);
    gfx_text_centered(LCD_W / 2, 172, bt, &Font12, col);
    gfx_text_centered(LCD_W / 2, 190, "tap to open", &Font12, RGB(70, 76, 90));
}

static void draw_bt() {
    char s[40];
    uint16_t col;
    const char *head = bt_summary(&col);
    bt_state_t st = ble_state();
    const char *host = ble_host_name();

    // Back chevron + title
    gfx_line(56, 31, 50, 25, 2.5f, COL_COPY);
    gfx_line(50, 25, 56, 19, 2.5f, COL_COPY);
    gfx_text_centered(LCD_W / 2, 18, "Bluetooth", &Font16, COL_TEXT);

    if (st == BT_PAIRING) {
        snprintf(s, sizeof s, "Pairing  %ds", ble_pair_secs_left());
        head = s;
    }
    gfx_text_centered(LCD_W / 2, 50, head, &Font16, col);

    switch (st) {
    case BT_UNPAIRED:
        gfx_text_centered(LCD_W / 2, 84, "Tap Pair, then on the PC:", &Font12, COL_DIM);
        gfx_text_centered(LCD_W / 2, 102, "Settings > Bluetooth >", &Font12, COL_TEXT);
        gfx_text_centered(LCD_W / 2, 118, "Add device >", &Font12, COL_TEXT);
        gfx_text_centered(LCD_W / 2, 134, "Bluetooth > Touch Deck", &Font12, COL_TEXT);
        button_at(BT_BTN1_X, BT_BTN_Y, BT_BTN1_W, BT_BTN_H, "Pair", &Font16, COL_COPY);
        break;
    case BT_PAIRING: {
        uint32_t k = ble_passkey();
        snprintf(s, sizeof s, "%03lu %03lu", (unsigned long)(k / 1000), (unsigned long)(k % 1000));
        gfx_rrect(56, 78, 128, 38, 10.f, COL_PANEL);
        gfx_text_centered(LCD_W / 2, 85, s, &Font24, COL_TEXT);
        gfx_text_centered(LCD_W / 2, 126, "Add device > Touch Deck", &Font12, COL_DIM);
        gfx_text_centered(LCD_W / 2, 142, "and enter this PIN", &Font12, COL_DIM);
        button_at(BT_BTN1_X, BT_BTN_Y, BT_BTN1_W, BT_BTN_H, "Cancel", &Font16, COL_PANEL);
        break;
    }
    default: {
        // Paired: who with, and what can be done about it.
        snprintf(s, sizeof s, "%.20s", host[0] ? host : "this PC");
        gfx_text_centered(LCD_W / 2, 80, st == BT_CONNECTED ? "Paired with" : "Paired to", &Font12, COL_DIM);
        gfx_text_centered(LCD_W / 2, 98, s, &Font16, COL_TEXT);
        const char *sub = st == BT_CONNECTED ? (ble_ready() ? "Keyboard + mouse ready" : "Connecting...")
                        : st == BT_WAITING  ? "PC will reconnect"
                                            : "Tap Connect to resume";
        gfx_text_centered(LCD_W / 2, 126, sub, &Font12, COL_DIM);
        if (st == BT_OFF) button_at(BTN_COPY_X, BT_BTN_Y, BTN_W, BT_BTN_H, "Connect", &Font12, COL_PASTE);
        else button_at(BTN_COPY_X, BT_BTN_Y, BTN_W, BT_BTN_H, "Disconnect", &Font12, COL_STOP);
        button_at(BTN_PASTE_X, BT_BTN_Y, BTN_W, BT_BTN_H, "Forget", &Font12, COL_FORGET);
        break;
    }
    }

    // Transient messages ("Paired", "Pairing timed out"...)
    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    bool show_msg = (int32_t)(app.msg_until_ms - now_ms()) > 0;
    if (show_msg) strcpy(s, app.msg);
    xSemaphoreGive(clip_mtx);
    if (show_msg) gfx_text_centered(LCD_W / 2, 214, s, &Font12, COL_TEXT);
}

static void page_dots(int screen, int count) {
    for (int i = 0; i < count; i++)
        gfx_disc(LCD_W / 2 + (i * 2 - (count - 1)) * 7, 232, 3.f, i == screen ? COL_TEXT : RGB(60, 64, 76));
}

void ui_task(void *) {
    uint32_t drawn_seq = ~0u, last_frame = 0;
    bool first = true;

    for (;;) {
        int screen = app.screen;
        uint32_t seq = app.redraw_seq;
        // Animate the jiggler dot / paste progress; otherwise refresh slowly
        // (status text, countdown). The C3 has no FPU, so keep frames modest.
        bool in_bt = app.in_bt;
        uint32_t period = (screen == SCR_JIG && app.jig_on) || app.clip_state != CLIP_IDLE ? 100 : 500;
        if (seq == drawn_seq && now_ms() - last_frame < period) {
            vTaskDelay(pdMS_TO_TICKS(5));
            continue;
        }
        drawn_seq = seq;
        last_frame = now_ms();

        gfx_fill(COL_BG);
        if (in_bt) {
            draw_bt();                 // sub-level: its own single page
            page_dots(0, 1);
        } else {
            if (screen == SCR_CLIP) draw_clip();
            else if (screen == SCR_JIG) draw_jig();
            else draw_settings();
            header_footer();
            page_dots(screen, SCR_COUNT);
        }

        display_push();
        display_wait();
        app.frames++;
        if (first) { display_backlight(200); first = false; }
    }
}
