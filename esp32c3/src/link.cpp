// PC -> board:  HELLO | PING           helper heartbeat (answered with PONG)
//               TIME hh:mm:ss          set the clock shown at the top
//               CLIP <n> <src>\n<n bytes>   new clip text
//               DBG                    reply with a one-line LOG of diagnostics
// board -> PC:  COPY                   user tapped COPY
//               LOG <text>             debug output
#include <Arduino.h>
#include "app.h"
#include "link.h"

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

void link_init() {
    Serial.begin(115200);
    Serial.setTxTimeoutMs(0);   // never block when no one is reading
}

void link_send_line(const char *s) {
    if (!app.helper) return;
    Serial.print(s);
    Serial.print('\n');
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
