// USB plumbing plus the line protocol spoken with tools/clip_helper.py.
//
// PC -> board:  HELLO | PING           helper heartbeat (answered with PONG)
//               TIME hh:mm:ss          set the watch
//               CLIP <n> <src>\n<n bytes>   new clip text
//               DBG                    reply with a one-line LOG of liveness/touch stats
//               VER                    reply VERSION <board> <version> <build date>
//               SWIPE L|R, BTN [LONG]  inject a swipe / BOOT-button press (scripting/tests)
//               ANIM 1|0               spin the jiggler page's dot without HID (perf tests)
//               BOOT                   reboot into the UF2 bootloader
// board -> PC:  COPY                   user tapped COPY
//               LOG <text>             debug output
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "tusb.h"
#include "pico/bootrom.h"
#include "app.h"
#include "usb_io.h"
#include "version.h"

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

void usb_send_line(const char *s) {
    if (!tud_cdc_connected()) return;
    tud_cdc_write_str(s);
    tud_cdc_write_char('\n');
    tud_cdc_write_flush();
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
    } else if (!strncmp(s, "ANIM ", 5)) {                          // perf tests: animate, no HID
        app.anim_demo = s[5] == '1';
        if (app.anim_demo) { extern void jiggler_demo_begin(void); jiggler_demo_begin(); }
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
    if (h != app.helper) app.helper = h;
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
