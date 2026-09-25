// CST816T capacitive touch on I2C1. The chip has its own gesture engine, but
// classifying raw points here gives consistent swipes + taps.
//
// Only talk to the chip right after it pulses TP_INT. Polling it on a timer
// addresses it while it is between scans / dozing, and within seconds it
// wedges mid-transfer holding SDA low, killing the bus. (Waveshare's demo
// reads on the interrupt too.) If the bus does wedge, bus_recover() clocks
// it free and the chip is reset and reconfigured.
#include <stdlib.h>
#include "pico/stdlib.h"
#include "hardware/i2c.h"
#include "board.h"
#include "touch.h"
#include "app.h"

#define TP_ADDR       0x15
#define REG_FINGERS   0x02
#define REG_CHIP_ID   0xA7
#define REG_IRQ_CTL   0xFA
#define REG_NO_SLEEP  0xFE

#define I2C_TIMEOUT_US 20000
#define RELEASE_MS    120    // no INT pulse for this long while down = finger lifted
#define SWIPE_MIN     45     // px of travel for a swipe
#define TAP_SLOP      18     // px a tap may wander
#define LONG_MS       700

touch_stats_t touch_stats;

static volatile bool int_pending;
static volatile uint32_t last_int_ms;
static bool down, long_fired;
static int sx, sy, lx, ly;
static uint32_t t_down, last_recover_ms;

static bool reg_write(uint8_t reg, uint8_t val) {
    uint8_t b[2] = {reg, val};
    return i2c_write_timeout_us(i2c1, TP_ADDR, b, 2, false, I2C_TIMEOUT_US) == 2;
}

static bool reg_read(uint8_t reg, uint8_t *buf, int n) {
    if (i2c_write_timeout_us(i2c1, TP_ADDR, &reg, 1, true, I2C_TIMEOUT_US) != 1) return false;
    return i2c_read_timeout_us(i2c1, TP_ADDR, buf, n, false, I2C_TIMEOUT_US) == n;
}

static void on_int(uint gpio, uint32_t events) {
    (void)events;
    if (gpio != TP_PIN_INT) return;
    int_pending = true;
    last_int_ms = now_ms();
    touch_stats.ints++;
}

// Standard I2C bus clear: a slave stuck mid-byte holds SDA low until it has
// clocked out its remaining bits, so pulse SCL (up to 9 times) until SDA is
// released, then issue a STOP. Pins are driven open-drain style: output-low
// or input with pull-up.
static void bus_recover(void) {
    gpio_set_function(I2C_PIN_SDA, GPIO_FUNC_SIO);
    gpio_set_function(I2C_PIN_SCL, GPIO_FUNC_SIO);
    gpio_put(I2C_PIN_SDA, 0);
    gpio_put(I2C_PIN_SCL, 0);
    gpio_set_dir(I2C_PIN_SDA, GPIO_IN);
    gpio_set_dir(I2C_PIN_SCL, GPIO_IN);
    for (int i = 0; i < 9 && !gpio_get(I2C_PIN_SDA); i++) {
        gpio_set_dir(I2C_PIN_SCL, GPIO_OUT); sleep_us(5);
        gpio_set_dir(I2C_PIN_SCL, GPIO_IN);  sleep_us(5);
    }
    gpio_set_dir(I2C_PIN_SDA, GPIO_OUT); sleep_us(5);   // STOP: SDA rises while SCL high
    gpio_set_dir(I2C_PIN_SDA, GPIO_IN);  sleep_us(5);
}

static bool chip_setup(void) {
    i2c_init(i2c1, 400 * 1000);
    gpio_set_function(I2C_PIN_SDA, GPIO_FUNC_I2C);
    gpio_set_function(I2C_PIN_SCL, GPIO_FUNC_I2C);
    gpio_pull_up(I2C_PIN_SDA);
    gpio_pull_up(I2C_PIN_SCL);

    gpio_put(TP_PIN_RST, 0); sleep_ms(10);
    gpio_put(TP_PIN_RST, 1); sleep_ms(60);

    // Right after reset the chip is awake, so these unsolicited accesses are safe.
    uint8_t id = 0;
    reg_read(REG_CHIP_ID, &id, 1);
    touch_stats.chip_id = id;
    reg_write(REG_NO_SLEEP, 0x01);
    reg_write(REG_IRQ_CTL, 0x60);    // INT pulses while touched and on touch/release
    down = false;
    int_pending = false;
    return id != 0;
}

bool touch_init(void) {
    gpio_init(TP_PIN_RST);
    gpio_set_dir(TP_PIN_RST, GPIO_OUT);
    gpio_init(TP_PIN_INT);
    gpio_set_dir(TP_PIN_INT, GPIO_IN);
    gpio_pull_up(TP_PIN_INT);
    bool ok = chip_setup();
    gpio_set_irq_enabled_with_callback(TP_PIN_INT, GPIO_IRQ_EDGE_FALL, true, on_int);
    return ok;
}

static void recover(void) {
    if (now_ms() - last_recover_ms < 500) return;
    last_recover_ms = now_ms();
    touch_stats.recoveries++;
    bus_recover();
    chip_setup();
}

int touch_diag_lines(void) {   // bit0 = SDA, bit1 = SCL (1 = high/idle)
    return gpio_get(I2C_PIN_SDA) | (gpio_get(I2C_PIN_SCL) << 1);
}

static touch_event_t released(void) {
    touch_event_t ev = {EV_NONE, sx, sy};
    down = false;
    if (long_fired) return ev;
    int dx = lx - sx, dy = ly - sy;
    if (abs(dx) >= SWIPE_MIN && abs(dx) > abs(dy)) ev.type = dx < 0 ? EV_SWIPE_L : EV_SWIPE_R;
    else if (abs(dy) >= SWIPE_MIN) ev.type = dy < 0 ? EV_SWIPE_U : EV_SWIPE_D;
    else if (abs(dx) < TAP_SLOP && abs(dy) < TAP_SLOP) ev.type = EV_TAP;
    return ev;
}

touch_event_t touch_poll(void) {
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
