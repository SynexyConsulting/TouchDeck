// USB plumbing plus the line protocol spoken with tools/clip_helper.py.
//
// PC -> board:  HELLO | PING           helper heartbeat (answered with PONG)
//               TIME hh:mm:ss          set the watch
//               CLIP <n> <src>\n<n bytes>   new clip text
//               DBG                    reply with a one-line LOG of liveness/touch stats
//               VER                    reply VERSION <board> <version> <build date>
//               SWIPE L|R, BTN [LONG]  inject a swipe / BOOT-button press (scripting/tests)
//               TAP x y                inject a tap (scripting/tests)
//               ANIM 1|0               spin the jiggler page's dot without HID (perf tests)
//               BOOT                   reboot into the UF2 bootloader
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
//               TEXT msg|src <text>    the transient message ("" = none), the clip's source
//               CLIPTEXT <escaped>     the clip's first 1024 bytes (\\ \n \r \t \xHH escapes)
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "tusb.h"
#include "pico/bootrom.h"
#include "app.h"
#include "usb_io.h"
#include "version.h"
#include "pico/binary_info.h"

// The board this firmware is for, readable in the UF2 file itself: the Windows app
// (Uf2.Inspect) refuses to install a file whose marker isn't the board it flashes.
// As binary info it is always kept by the linker (and shown by picotool info).
bi_decl(bi_program_feature("TDBOARD:" FW_BOARD ";"))
#include "jig_paths.h"
#include "jiggler.h"
#include "settings.h"
#include "ui.h"
#include "ui_sync.h"
#include "gfx.h"
#include "board.h"

#define HELPER_TIMEOUT_MS 5000

static uint8_t kbd_leds;
static uint32_t helper_seen_ms;
static bool helper_seen_ever;

static char line[64];
static int line_len;
static char rx_clip[CLIP_MAX];
static char rx_src[12];
static int rx_want, rx_got;       // rx_want > 0 while receiving a CLIP payload

void usb_io_init(void) {
    tud_init(0);
}

bool usb_hid_ready(void) { return tud_mounted() && tud_hid_ready(); }
bool usb_caps_lock(void) { return kbd_leds & KEYBOARD_LED_CAPSLOCK; }

bool usb_key(uint8_t modifier, uint8_t keycode) {
    uint8_t keys[6] = {keycode, 0, 0, 0, 0, 0};
    return tud_hid_keyboard_report(REPORT_ID_KEYBOARD, keycode ? modifier : 0, keys);
}

bool usb_mouse(uint8_t buttons, int8_t dx, int8_t dy) {
    return tud_hid_mouse_report(REPORT_ID_MOUSE, buttons, dx, dy, 0, 0);
}

// Sends the whole line: a line can be longer than the free TX FIFO (DBG is ~300
// bytes), so write what fits, flush, and let USB drain it. Bounded, so a host
// that stops reading can't stall core0.
static bool write_all(const char *p, size_t n, uint32_t give_up) {
    while (n) {
        uint32_t w = tud_cdc_write(p, n);
        p += w;
        n -= w;
        if (!n) break;
        tud_cdc_write_flush();
        if ((int32_t)(now_ms() - give_up) >= 0) return false;
        tud_task();
    }
    return true;
}

bool usb_send_line(const char *s) {
    if (!tud_cdc_connected()) return false;
    uint32_t give_up = now_ms() + 20;
    bool ok = write_all(s, strlen(s), give_up) && write_all("\n", 1, give_up);
    tud_cdc_write_flush();
    return ok;
}

// The app's device mirror (WATCH 1): STATE/TEXT/CLIPTEXT lines from a sample
// of the UI state, checked every 10 ms and sent on change (ui_sync.c). The dot
// alone is sent at most every 50 ms, the device's own animation rate.
static bool watching, sync_force;
static ui_state_t sync_last, sync_cur;          // static: 1.2 KB is too much for core0's stack
static uint32_t sync_check_ms, sync_state_ms, sync_clip_seq;
static char sync_line[UI_SYNC_CLIP_LINE];

static bool sync_text(int d, int bit, const char *key, char *last, const char *cur, size_t n) {
    if (!(d & bit)) return true;
    ui_sync_text_line(key, cur, sync_line, sizeof sync_line);
    if (!usb_send_line(sync_line)) return false;
    memcpy(last, cur, n);
    return true;
}

void usb_state_poll(void) {
    if (!tud_cdc_connected()) watching = false;   // the app closed the port: it sends WATCH 1 again
    if (!watching) return;
    uint32_t now = now_ms();
    if (now - sync_check_ms < 10) return;
    sync_check_ms = now;
    if (tud_cdc_write_available() < 64) return;   // the host isn't reading: try later, don't stall core0
    ui_state_fill(&sync_cur, 0);
    int d = sync_force ? UI_SYNC_ALL : ui_sync_diff(&sync_last, &sync_cur);
    // Each part counts as sent only once its whole line is out; anything that
    // didn't make it still differs from sync_last and goes again next time.
    bool ok = true;
    ok &= sync_text(d, UI_SYNC_MSG, "msg", sync_last.msg, sync_cur.msg, sizeof sync_last.msg);
    ok &= sync_text(d, UI_SYNC_SRC, "src", sync_last.clip_src, sync_cur.clip_src, sizeof sync_last.clip_src);
    if (sync_force || app.clip_seq != sync_clip_seq) {
        mutex_enter_blocking(&clip_mtx);
        uint32_t seq = app.clip_seq;
        ui_sync_clip_line(app.clip, app.clip_len, sync_line, sizeof sync_line);
        mutex_exit(&clip_mtx);
        if (usb_send_line(sync_line)) sync_clip_seq = seq;
        else ok = false;
    }
    if ((d & UI_SYNC_FIELDS) || ((d & UI_SYNC_DOT) && now - sync_state_ms >= 50)) {
        ui_sync_state_line(&sync_cur, sync_line, sizeof sync_line);
        if (usb_send_line(sync_line)) {
            ui_sync_commit_fields(&sync_last, &sync_cur);   // the numbers; strings go with TEXT
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

static void commit_clip(void) {
    if (app.clip_state == CLIP_PASTING) {
        app_message("Busy pasting");
        return;
    }
    mutex_enter_blocking(&clip_mtx);
    memcpy(app.clip, rx_clip, rx_got);
    app.clip[rx_got] = 0;
    app.clip_len = rx_got;
    app.clip_seq++;
    strcpy(app.clip_src, rx_src);
    mutex_exit(&clip_mtx);
    app.clip_state = CLIP_IDLE;

    char m[40];
    if (rx_got == 0) snprintf(m, sizeof m, "Nothing selected");
    else snprintf(m, sizeof m, "Copied %d chars", rx_got);
    app_message(m);
}

static void handle_line(char *s) {
    helper_seen_ms = now_ms();
    helper_seen_ever = true;

    if (!strcmp(s, "HELLO") || !strcmp(s, "PING")) {
        usb_send_line("PONG");
    } else if (!strncmp(s, "TIME ", 5)) {
        int h, m, sec;
        if (sscanf(s + 5, "%d:%d:%d", &h, &m, &sec) == 3) {
            extern void clock_set(int seconds_of_day);
            clock_set(h * 3600 + m * 60 + sec);
        }
    } else if (!strncmp(s, "CLIP ", 5)) {
        int n = 0;
        char src[12] = "pc";
        sscanf(s + 5, "%d %11s", &n, src);
        strcpy(rx_src, src);
        rx_got = 0;
        rx_want = n;
        if (n == 0) commit_clip();
    } else if (!strcmp(s, "VER")) {
        usb_send_line("VERSION " FW_BOARD " " FW_VERSION " " __DATE__);
    } else if (!strcmp(s, "DBG")) {
        extern void debug_report(void);
        debug_report();
    } else if (!strcmp(s, "SWIPE L") || !strcmp(s, "SWIPE R")) {   // scripting / tests
        extern void inject_swipe(bool left);
        inject_swipe(s[6] == 'L');
    } else if (!strcmp(s, "BTN") || !strcmp(s, "BTN LONG")) {       // scripting / tests
        extern void inject_button(bool long_press);
        inject_button(s[3] == ' ');
    } else if (!strncmp(s, "TAP ", 4)) {                           // scripting / tests
        int x, y;
        extern void inject_tap(int x, int y);
        if (sscanf(s + 4, "%d %d", &x, &y) == 2) inject_tap(x, y);
    } else if (!strncmp(s, "ANIM ", 5)) {                          // perf tests: animate, no HID
        app.anim_demo = s[5] == '1';
        if (app.anim_demo) { extern void jiggler_demo_begin(void); jiggler_demo_begin(); }
    } else if (!strcmp(s, "WATCH 1") || !strcmp(s, "WATCH 0")) {
        watching = s[6] == '1';
        sync_force = watching;                // everything once, right away
    } else if (!strcmp(s, "JIG MENU")) {                            // tests: menu sequence now
        jiggler_menu_now();
    } else if (!strcmp(s, "JIG ON") || !strcmp(s, "JIG OFF")) {
        jiggler_set(s[5] == 'N');
        settings_save();
    } else if (!strncmp(s, "JIG SCALE ", 10)) {
        int n = s[10] - '0';
        if (n >= 0 && n < JIG_SCALE_COUNT && !s[11]) {
            app.jig_scale_idx = n;
            app_redraw();
            settings_save();
        }
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
    } else if (!strncmp(s, "FBCRC ", 6)) {                         // tests: mirror == device
        int x, y, w, h;
        if (sscanf(s + 6, "%d %d %d %d", &x, &y, &w, &h) == 4 && x >= 0 && y >= 0 && w > 0 && h > 0 &&
            x + w <= LCD_W && y + h <= LCD_H) {
            char m[32];
            snprintf(m, sizeof m, "LOG fbcrc %08lx", (unsigned long)fb_crc32(x, y, w, h));
            usb_send_line(m);
        }
    } else if (!strcmp(s, "BOOT")) {
        reset_usb_boot(0, 0);
    }
}

static void feed(const uint8_t *buf, int n) {
    for (int i = 0; i < n; i++) {
        if (rx_want > 0) {
            if (rx_got < CLIP_MAX) rx_clip[rx_got++] = (char)buf[i];
            if (--rx_want == 0) commit_clip();
            continue;
        }
        char c = (char)buf[i];
        if (c == '\r') continue;
        if (c == '\n') {
            line[line_len] = 0;
            if (line_len) handle_line(line);
            line_len = 0;
        } else if (line_len < (int)sizeof line - 1) {
            line[line_len++] = c;
        }
    }
}

void usb_io_poll(void) {
    tud_task();
    uint8_t buf[64];
    while (tud_cdc_available()) {
        int n = (int)tud_cdc_read(buf, sizeof buf);
        if (n <= 0) break;
        feed(buf, n);
    }
    bool h = tud_cdc_connected() && helper_seen_ever &&
             (now_ms() - helper_seen_ms) < HELPER_TIMEOUT_MS;
    if (h != app.helper) {             // the watch shows "PC" while the app talks to us
        app.helper = h;
        app_redraw();
    }
}

// Opening the port at 1200 baud is the Arduino-style "reboot to bootloader".
void tud_cdc_line_coding_cb(uint8_t itf, cdc_line_coding_t const *coding) {
    (void)itf;
    if (coding->bit_rate == 1200) reset_usb_boot(0, 0);
}

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t report_type,
                               uint8_t *buffer, uint16_t reqlen) {
    (void)instance; (void)report_id; (void)report_type; (void)buffer; (void)reqlen;
    return 0;
}

// The host's only message to a keyboard: lock-key LED state.
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t report_type,
                           uint8_t const *buffer, uint16_t bufsize) {
    (void)instance;
    if (report_type != HID_REPORT_TYPE_OUTPUT || bufsize < 1) return;
    if (report_id == REPORT_ID_KEYBOARD) kbd_leds = buffer[bufsize - 1];
    else if (report_id == 0 && bufsize >= 2 && buffer[0] == REPORT_ID_KEYBOARD) kbd_leds = buffer[1];
}
