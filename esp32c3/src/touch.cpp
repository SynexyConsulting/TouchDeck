// CST816D touch on I2C. Same rule as the RP2040 board's CST816T: only talk to
// the chip right after it pulses TP_INT. Timer polling addresses it while it
// is between scans / dozing and it wedges mid-transfer with SDA held low.
// If the bus does wedge, bus_recover() clocks it free and the chip is reset.
#include <Arduino.h>
#include <Wire.h>
#include "board.h"
#include "touch.h"
#include "app.h"

#define TP_ADDR       0x15
#define REG_FINGERS   0x02
#define REG_CHIP_ID   0xA7
#define REG_IRQ_CTL   0xFA
#define REG_NO_SLEEP  0xFE

#define RELEASE_MS    120    // no INT pulse for this long while down = finger lifted
#define SWIPE_MIN     40     // px of travel for a swipe
#define TAP_SLOP      18     // px a tap may wander
#define LONG_MS       700

touch_stats_t touch_stats;

static volatile bool int_pending;
static uint32_t last_int_ms;
static bool down, long_fired;
static int sx, sy, lx, ly;
static uint32_t t_down, last_recover_ms;

static bool reg_write(uint8_t reg, uint8_t val) {
    Wire.beginTransmission(TP_ADDR);
    Wire.write(reg);
    Wire.write(val);
    return Wire.endTransmission() == 0;
}

static bool reg_read(uint8_t reg, uint8_t *buf, int n) {
    Wire.beginTransmission(TP_ADDR);
    Wire.write(reg);
    if (Wire.endTransmission(false) != 0) return false;
    if (Wire.requestFrom((uint8_t)TP_ADDR, (uint8_t)n) != n) return false;
    for (int i = 0; i < n; i++) buf[i] = Wire.read();
    return true;
}

static void IRAM_ATTR on_int() {
    int_pending = true;
    touch_stats.ints++;
}

// Standard I2C bus clear: pulse SCL (up to 9 times) until the stuck slave
// releases SDA, then issue a STOP. Pins driven open-drain style.
static void bus_recover() {
    Wire.end();
    pinMode(TP_PIN_SDA, INPUT_PULLUP);
    pinMode(TP_PIN_SCL, INPUT_PULLUP);
    for (int i = 0; i < 9 && !digitalRead(TP_PIN_SDA); i++) {
        pinMode(TP_PIN_SCL, OUTPUT); digitalWrite(TP_PIN_SCL, LOW); delayMicroseconds(5);
        pinMode(TP_PIN_SCL, INPUT_PULLUP); delayMicroseconds(5);
    }
    pinMode(TP_PIN_SDA, OUTPUT); digitalWrite(TP_PIN_SDA, LOW); delayMicroseconds(5);
    pinMode(TP_PIN_SDA, INPUT_PULLUP); delayMicroseconds(5);   // STOP
}

static bool chip_setup() {
    Wire.begin(TP_PIN_SDA, TP_PIN_SCL, 400000);
    Wire.setTimeOut(20);

    digitalWrite(TP_PIN_RST, LOW); delay(10);
    digitalWrite(TP_PIN_RST, HIGH); delay(60);

    // Right after reset the chip is awake, so these unsolicited accesses are safe.
    uint8_t id = 0;
    reg_read(REG_CHIP_ID, &id, 1);
    touch_stats.chip_id = id;
    reg_write(REG_NO_SLEEP, 0x01);
    reg_write(REG_IRQ_CTL, 0x60);   // INT pulses while touched and on touch/release
    down = false;
    int_pending = false;
    return id != 0;
}

bool touch_init() {
    pinMode(TP_PIN_RST, OUTPUT);
    pinMode(TP_PIN_INT, INPUT_PULLUP);
    bool ok = chip_setup();
    attachInterrupt(digitalPinToInterrupt(TP_PIN_INT), on_int, FALLING);
    return ok;
}

static void recover() {
    if (now_ms() - last_recover_ms < 500) return;
    last_recover_ms = now_ms();
    touch_stats.recoveries++;
    bus_recover();
    chip_setup();
}

int touch_diag_lines() {
    return digitalRead(TP_PIN_SDA) | (digitalRead(TP_PIN_SCL) << 1);
}

static touch_event_t released() {
    touch_event_t ev = {EV_NONE, sx, sy};
    down = false;
    if (long_fired) return ev;
    int dx = lx - sx, dy = ly - sy;
    if (abs(dx) >= SWIPE_MIN && abs(dx) > abs(dy)) ev.type = dx < 0 ? EV_SWIPE_L : EV_SWIPE_R;
    else if (abs(dy) >= SWIPE_MIN) ev.type = dy < 0 ? EV_SWIPE_U : EV_SWIPE_D;
    else if (abs(dx) < TAP_SLOP && abs(dy) < TAP_SLOP) ev.type = EV_TAP;
    return ev;
}

touch_event_t touch_poll() {
    touch_event_t ev = {EV_NONE, 0, 0};

    if (!int_pending) {
        // The chip stops pulsing when the finger lifts; don't poke it to ask.
        if (down && now_ms() - last_int_ms > RELEASE_MS) return released();
        if (down && !long_fired && now_ms() - t_down >= LONG_MS &&
            abs(lx - sx) < TAP_SLOP && abs(ly - sy) < TAP_SLOP) {
            long_fired = true;
            return (touch_event_t){EV_LONG, sx, sy};
        }
        return ev;
    }
    int_pending = false;
    last_int_ms = now_ms();

    uint8_t b[5];
    touch_stats.reads++;
    if (!reg_read(REG_FINGERS, b, 5)) {
        touch_stats.fails++;
        recover();
        return ev;
    }

    bool pressed = (b[0] & 0x0F) != 0;
    int x = ((b[1] & 0x0F) << 8) | b[2];
    int y = ((b[3] & 0x0F) << 8) | b[4];

    if (!pressed) return down ? released() : ev;

    touch_stats.last_x = x;
    touch_stats.last_y = y;
    if (!down) {
        down = true;
        long_fired = false;
        sx = lx = x;
        sy = ly = y;
        t_down = now_ms();
        touch_stats.presses++;
    } else {
        lx = x;
        ly = y;
    }
    return ev;
}
