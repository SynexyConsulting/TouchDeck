#include "app.h"
#include "ble_hid.h"
#include "jiggler.h"
#include "typer.h"

// BLE delivers reports once per connection interval (7.5-15 ms on Windows),
// so pace keys slower than over USB or the host coalesces/drops them.
#define HOLD_MS 12
#define GAP_MS  12

static bool pending, active, key_down;
static int pos;
static uint32_t next_ms;

// US-layout ASCII -> HID usage. Returns false for untypeable characters.
static bool ascii_to_hid(char c, uint8_t *code, bool *shift) {
    static const char unshifted[] = "-=[]\\;'`,./";
    static const char shifted[] = "_+{}|:\"~<>?";
    static const uint8_t codes[] = {0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38};
    static const char digit_shift[] = ")!@#$%^&*(";

    *shift = false;
    if (c >= 'a' && c <= 'z') { *code = 0x04 + (c - 'a'); return true; }
    if (c >= 'A' && c <= 'Z') { *code = 0x04 + (c - 'A'); *shift = true; return true; }
    if (c >= '1' && c <= '9') { *code = 0x1E + (c - '1'); return true; }
    if (c == '0') { *code = 0x27; return true; }
    if (c == '\n') { *code = 0x28; return true; }
    if (c == '\t') { *code = 0x2B; return true; }
    if (c == ' ') { *code = 0x2C; return true; }
    for (int i = 0; unshifted[i]; i++) {
        if (c == unshifted[i]) { *code = codes[i]; return true; }
        if (c == shifted[i]) { *code = codes[i]; *shift = true; return true; }
    }
    for (int d = 0; d < 10; d++)
        if (c == digit_shift[d]) { *code = d == 0 ? 0x27 : 0x1E + d - 1; *shift = true; return true; }
    return false;
}

void typer_start() {
    if (active || pending || app.clip_len == 0) return;
    if (!ble_ready()) { app_message("Bluetooth not connected"); return; }
    pending = true;
    app.clip_state = CLIP_PASTING;
    app.paste_pos = 0;
}

static void finish() {
    pending = active = false;
    app.clip_state = CLIP_IDLE;
    app.jig_paused = false;
}

void typer_cancel() {
    if (key_down) ble_key(0, 0);   // never leave a key stuck down
    key_down = false;
    finish();
    app_message("Paste stopped");
}

bool typer_busy() { return pending || active; }

// Next typeable character at pos, or -1 at the end. CRLF and lone CR become Enter.
static int next_char(uint8_t *code, bool *shift) {
    while (pos < app.clip_len) {
        char c = app.clip[pos++];
        if (c == '\r') {
            if (pos < app.clip_len && app.clip[pos] == '\n') continue;
            c = '\n';
        }
        if (ascii_to_hid(c, code, shift)) return (unsigned char)c;
    }
    return -1;
}

void typer_step() {
    if (pending) {
        if (!jiggler_idle()) return;   // let a right-click/ESC sequence finish
        app.jig_paused = true;
        pending = false;
        active = true;
        key_down = false;
        pos = 0;
        next_ms = now_ms();
    }
    if (!active) return;
    if (!ble_ready()) { finish(); app_message("Bluetooth lost"); return; }
    if ((int32_t)(now_ms() - next_ms) < 0) return;

    if (key_down) {
        ble_key(0, 0);
        key_down = false;
        next_ms = now_ms() + GAP_MS;
        app.paste_pos = pos;
        if (pos >= app.clip_len) { finish(); app_message("Pasted"); }
        return;
    }

    uint8_t code;
    bool shift;
    int c = next_char(&code, &shift);
    if (c < 0) { finish(); app_message("Pasted"); return; }
    // Caps Lock on the host flips letter case, so compensate.
    if (ble_caps_lock() && isalpha(c)) shift = !shift;
    ble_key(shift ? KEY_MOD_LSHIFT : 0, code);
    key_down = true;
    next_ms = now_ms() + HOLD_MS;
}
