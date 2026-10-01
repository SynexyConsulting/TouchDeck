// PC -> board:  HELLO | PING           helper heartbeat (answered with PONG)
//               TIME hh:mm:ss          set the clock shown at the top
//               CLIP <n> <src>\n<n bytes>   new clip text
//               DBG                    reply with a one-line LOG of diagnostics
//               VER                    reply VERSION <board> <version> <build date>
//               LEDS <hex>             PC lock-key state (bit1 = Caps Lock)
//               MODE PC|BT             set the output mode (scripting/tests)
//               TAP x y | SWIPE L|R    inject a touch event (scripting/tests)
//               TEXTW <font> <sp> <t>  reply LOG textw <px> (tests: C vs fontgen widths)
//               ANIM 1|0               walk a jiggler letter on screen without HID (tests)
//               WATCH 1|0              start/stop STATE reports (the Windows app's mirror)
//               JIG ON|OFF, JIG SCALE n   jiggler on/off, scale index 0-2 (saved)
//               CLIP CLEAR             empty the clip (ignored when empty or pasting)
//               JIG MENU               start the right-click/Esc sequence now (tests)
//               JIG CFG m k o p        Jiggler settings: context menu 0|1, key 0=ESC 1=F15,
//                                      menu open 0-60 s, pause before the next letter 0-60 s
//                                      (saved; STATE reports them as jmenu= jkey= jopen= jpause=)
//               FBCRC x y w h          reply LOG fbcrc <hex>: CRC-32 of that framebuffer region
//                                      (tests: the app's mirror draws the same pixels)
// board -> PC:  COPY                   user tapped COPY
//               LOG <text>             debug output
//   After WATCH 1, the device mirror (ui_sync.c, docs/superpowers/specs/2026-09-29-device-mirror-design.md):
//   everything once, then on change.
//               STATE jig= letter= scale= phase= x= y= clip= paste=   (the 1.6.0 fields, then 1.7.0's)
//                     page= sub= t= pc= link= mute= timer= cst= ppos= paused= demo= next= up= menus=
//                     mode= bta= bts= btr= left= pk=     the dot (x, y) at most every 50 ms
//               TEXT msg|src|host|down <text>   the transient message ("" = none), the clip's
//                                      source, the bonded PC's name, why output isn't ready
//               CLIPTEXT <escaped>     the clip's first 1024 bytes (\\ \n \r \t \xHH escapes)
//               K <mod> <usage>        keyboard report, PC output mode (hex)
//               M <btn> <dx> <dy>      mouse report, PC output mode (hex, dec, dec)
#include <Arduino.h>
#include "app.h"
#include "link.h"
#include "output.h"
#include "touch.h"
#include "gfx.h"
#include "version.h"
#include "jig_paths.h"
#include "jiggler.h"
#include "board.h"
#include "ui.h"
#include "ui_sync.h"

#define HELPER_TIMEOUT_MS 5000

static uint32_t helper_seen_ms;
static bool helper_seen_ever;
static char line[64];
static int line_len;
static char rx_clip[CLIP_MAX];
static char rx_src[12];
static int rx_want, rx_got;

extern void debug_report();
extern void clock_set(int seconds_of_day);
extern void inject_touch(int type, int x, int y);

void link_init() {
    // Room for the mirror's longest line (CLIPTEXT, up to ~4 KB) plus K/M reports.
    // Must precede begin(), which otherwise makes a 256-byte ring.
    Serial.setTxBufferSize(8192);
    Serial.begin(115200);
    Serial.setTxTimeoutMs(0);   // never block when no one is reading
}

// Whole lines or nothing: with no timeout, a line that doesn't fit would be cut
// and glued to the next one.
bool link_send_line(const char *s) {
    if (!app.helper) return false;
    size_t n = strlen(s);
    if (Serial.availableForWrite() < (int)n + 1) return false;
    Serial.write((const uint8_t *)s, n);
    Serial.write('\n');
    return true;
}

extern void jig_set_scale(int idx);
extern void jig_set_on(bool on);

// The app's device mirror (WATCH 1): STATE/TEXT/CLIPTEXT lines from a sample
// of the UI state, checked every 10 ms and sent on change (ui_sync.c). The dot
// alone is sent at most every 50 ms, the device's own animation rate.
static bool watching, sync_force;
static ui_state_t sync_last, sync_cur;
static uint32_t sync_check_ms, sync_state_ms, sync_clip_seq;
static char sync_line[UI_SYNC_CLIP_LINE];

static bool sync_text(int d, int bit, const char *key, char *last, const char *cur, size_t n) {
    if (!(d & bit)) return true;
    ui_sync_text_line(key, cur, sync_line, sizeof sync_line);
    if (!link_send_line(sync_line)) return false;
    memcpy(last, cur, n);
    return true;
}

void link_state_poll() {
    if (!watching || !app.helper) return;
    uint32_t now = now_ms();
    if (now - sync_check_ms < 10) return;
    sync_check_ms = now;
    ui_state_fill(&sync_cur, false);
    int d = sync_force ? UI_SYNC_ALL : ui_sync_diff(&sync_last, &sync_cur);
    // Each part counts as sent only once its whole line is out; anything that
    // didn't make it still differs from sync_last and goes again next time.
    bool ok = true;
    ok &= sync_text(d, UI_SYNC_MSG, "msg", sync_last.msg, sync_cur.msg, sizeof sync_last.msg);
    ok &= sync_text(d, UI_SYNC_SRC, "src", sync_last.clip_src, sync_cur.clip_src, sizeof sync_last.clip_src);
    ok &= sync_text(d, UI_SYNC_HOST, "host", sync_last.bt_host, sync_cur.bt_host, sizeof sync_last.bt_host);
    ok &= sync_text(d, UI_SYNC_DOWN, "down", sync_last.down_reason, sync_cur.down_reason, sizeof sync_last.down_reason);
    if (sync_force || app.clip_seq != sync_clip_seq) {
        xSemaphoreTake(clip_mtx, portMAX_DELAY);
        uint32_t seq = app.clip_seq;
        ui_sync_clip_line(app.clip, app.clip_len, sync_line, sizeof sync_line);
        xSemaphoreGive(clip_mtx);
        if (link_send_line(sync_line)) sync_clip_seq = seq;
        else ok = false;
    }
    if ((d & UI_SYNC_FIELDS) || ((d & UI_SYNC_DOT) && now - sync_state_ms >= 50)) {
        ui_sync_state_line(&sync_cur, sync_line, sizeof sync_line);
        if (link_send_line(sync_line)) {
            memcpy(&sync_last, &sync_cur, offsetof(ui_state_t, clip_src));   // the numbers; strings go with TEXT
            sync_state_ms = now;
        } else ok = false;
    }
    if (ok) sync_force = false;                   // after WATCH 1, until everything went out once
}

// CRC-32 (zlib's) of a framebuffer region, pixels as little-endian RGB565 bytes, row by row.
static uint32_t fb_crc32(int x, int y, int w, int h) {
    uint32_t crc = 0xFFFFFFFFu;
    for (int r = y; r < y + h; r++) {
        const uint8_t *p = (const uint8_t *)&fb[r * LCD_W + x];
        for (int i = 0; i < w * 2; i++) {
            crc ^= p[i];
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
        }
    }
    return ~crc;
}

static void commit_clip() {
    if (app.clip_state == CLIP_PASTING) {
        app_message("Busy pasting");
        return;
    }
    xSemaphoreTake(clip_mtx, portMAX_DELAY);
    memcpy(app.clip, rx_clip, rx_got);
    app.clip[rx_got] = 0;
    app.clip_len = rx_got;
    app.clip_seq++;
    strcpy(app.clip_src, rx_src);
    xSemaphoreGive(clip_mtx);
    app.clip_state = CLIP_IDLE;

    char m[40];
    if (rx_got == 0) snprintf(m, sizeof m, "Nothing selected");
    else snprintf(m, sizeof m, "Copied %d chars", rx_got);
    app_message(m);
}

static void handle_line(char *s) {
    helper_seen_ms = now_ms();
    helper_seen_ever = true;
    app.helper = true;

    if (!strcmp(s, "HELLO") || !strcmp(s, "PING")) {
        link_send_line("PONG");
    } else if (!strncmp(s, "TIME ", 5)) {
        int h, m, sec;
        if (sscanf(s + 5, "%d:%d:%d", &h, &m, &sec) == 3) clock_set(h * 3600 + m * 60 + sec);
    } else if (!strncmp(s, "CLIP ", 5)) {
        int n = 0;
        char src[12] = "pc";
        sscanf(s + 5, "%d %11s", &n, src);
        strcpy(rx_src, src);
        rx_got = 0;
        rx_want = n;
        if (n == 0) commit_clip();
    } else if (!strncmp(s, "LEDS ", 5)) {
        app.pc_leds = (uint8_t)strtol(s + 5, nullptr, 16);
    } else if (!strncmp(s, "MODE ", 5)) {       // scripting / tests
        mode_set(!strcmp(s + 5, "BT") ? MODE_BT : MODE_PC);
    } else if (!strncmp(s, "TAP ", 4)) {        // scripting / tests: inject a tap
        int x, y;
        if (sscanf(s + 4, "%d %d", &x, &y) == 2) inject_touch(EV_TAP, x, y);
    } else if (!strcmp(s, "SWIPE L")) {
        inject_touch(EV_SWIPE_L, 120, 120);
    } else if (!strcmp(s, "SWIPE R")) {
        inject_touch(EV_SWIPE_R, 120, 120);
    } else if (!strncmp(s, "TEXTW ", 6)) {
        static const struct { const char *name; const aa_font_t *f; } fonts[] = {
            {"font_title", &font_title}, {"font_label", &font_label}, {"font_button", &font_button},
            {"font_body", &font_body}, {"font_caps", &font_caps}, {"font_big", &font_big},
            {"font_mono", &font_mono}, {"font_pin", &font_pin}};
        char name[16];
        int sp, used = 0;
        if (sscanf(s + 6, "%15s %d %n", name, &sp, &used) == 2 && used)
            for (auto &e : fonts)
                if (!strcmp(e.name, name)) {
                    char r[32];
                    snprintf(r, sizeof r, "LOG textw %d", gfx_text_aa_width(s + 6 + used, e.f, sp));
                    link_send_line(r);
                }
    } else if (!strncmp(s, "ANIM ", 5)) {
        app.anim_demo = s[5] == '1';
        if (app.anim_demo) jiggler_demo_begin();
        app_redraw();
    } else if (!strcmp(s, "WATCH 1") || !strcmp(s, "WATCH 0")) {
        watching = s[6] == '1';
        sync_force = watching;                // everything once, right away
    } else if (!strcmp(s, "JIG MENU")) {           // tests: menu sequence now
        jiggler_menu_now();
    } else if (!strcmp(s, "JIG ON") || !strcmp(s, "JIG OFF")) {
        jig_set_on(s[5] == 'N');
    } else if (!strncmp(s, "JIG SCALE ", 10)) {
        int n = s[10] - '0';
        if (n >= 0 && n < JIG_SCALE_COUNT && !s[11]) jig_set_scale(n);
    } else if (!strncmp(s, "JIG CFG ", 8)) {      // Jiggler settings: menu key open pause
        int mo, k, o, p;
        char extra;
        if (sscanf(s + 8, "%d %d %d %d %c", &mo, &k, &o, &p, &extra) == 4 && (mo == 0 || mo == 1) &&
            (k == 0 || k == 1) && o >= 0 && o <= JM_MAX_S && p >= 0 && p <= JM_MAX_S) {
            jig_cfg_t c = {(uint8_t)mo, (uint8_t)k, (uint8_t)o, (uint8_t)p};
            jig_set_cfg(&c);
        }
    } else if (!strcmp(s, "CLIP CLEAR")) {
        clip_clear();
    } else if (!strcmp(s, "VER")) {
        link_send_line("VERSION " FW_BOARD " " FW_VERSION " " __DATE__);
    } else if (!strncmp(s, "FBCRC ", 6)) {         // tests: mirror == device
        int x, y, w, h;
        if (sscanf(s + 6, "%d %d %d %d", &x, &y, &w, &h) == 4 && x >= 0 && y >= 0 && w > 0 && h > 0 &&
            x + w <= LCD_W && y + h <= LCD_H) {
            char m[32];
            snprintf(m, sizeof m, "LOG fbcrc %08lx", (unsigned long)fb_crc32(x, y, w, h));
            link_send_line(m);
        }
    } else if (!strcmp(s, "DBG")) {
        debug_report();
    }
}

void link_poll() {
    while (Serial.available()) {
        int c = Serial.read();
        if (c < 0) break;
        if (rx_want > 0) {
            if (rx_got < CLIP_MAX) rx_clip[rx_got++] = (char)c;
            if (--rx_want == 0) commit_clip();
            continue;
        }
        if (c == '\r') continue;
        if (c == '\n') {
            line[line_len] = 0;
            if (line_len) handle_line(line);
            line_len = 0;
        } else if (line_len < (int)sizeof line - 1) {
            line[line_len++] = (char)c;
        }
    }
    bool h = helper_seen_ever && now_ms() - helper_seen_ms < HELPER_TIMEOUT_MS;
    if (h != app.helper) {
        app.helper = h;
        app_redraw();
    }
}
