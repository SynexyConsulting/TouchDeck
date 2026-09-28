// BOOT is wired to the flash chip's QSPI chip-select, so reading it means
// briefly letting CS float and sampling it (the Pico SDK examples' BOOTSEL
// trick). While CS floats the flash is unusable: the sample runs from RAM,
// inside flash_safe_execute, which also parks core1 (it renders from flash).
#include "pico/stdlib.h"
#include "pico/flash.h"
#include "hardware/structs/ioqspi.h"
#include "hardware/structs/sio.h"
#include "hardware/sync.h"
#include "app.h"
#include "button.h"

#define LONG_MS 800

static bool stable, last_raw, long_fired;
static uint32_t pressed_ms;

static void __no_inline_not_in_flash_func(sample_bootsel)(void *out) {
    const uint CS = 1;   // QSPI_SS is io[1] of the QSPI bank
    hw_write_masked(&ioqspi_hw->io[CS].ctrl, GPIO_OVERRIDE_LOW << IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_LSB,
                    IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_BITS);
    for (volatile int i = 0; i < 1000; ++i) {}   // let the line settle
    *(bool *)out = !(sio_hw->gpio_hi_in & (1u << CS));   // pressed pulls CS low
    hw_write_masked(&ioqspi_hw->io[CS].ctrl, GPIO_OVERRIDE_NORMAL << IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_LSB,
                    IO_QSPI_GPIO_QSPI_SS_CTRL_OEOVER_BITS);
}

static bool read_raw(void) {
    bool pressed = false;
    if (flash_safe_execute(sample_bootsel, &pressed, 5) != PICO_OK) return last_raw;
    return pressed;
}

void button_init(void) {
    stable = last_raw = read_raw();
}

btn_ev_t button_poll(void) {
    bool raw = read_raw();
    bool settled = raw == last_raw;        // two equal samples 20 ms apart = debounced
    last_raw = raw;
    if (!settled) return BTN_NONE;

    if (raw && !stable) {                  // press
        stable = true;
        long_fired = false;
        pressed_ms = now_ms();
    } else if (!raw && stable) {           // release
        stable = false;
        return long_fired ? BTN_NONE : BTN_SHORT;
    } else if (raw && !long_fired && now_ms() - pressed_ms >= LONG_MS) {
        long_fired = true;
        return BTN_LONG;
    }
    return BTN_NONE;
}
