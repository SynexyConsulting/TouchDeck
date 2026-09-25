#include "buzzer.h"
#include "board.h"
#include "pico/stdlib.h"
#include "hardware/pwm.h"
#include "hardware/clocks.h"

static uint slice, chan;
static alarm_id_t stop_alarm;

void buzzer_init(void) {
    gpio_set_function(BUZZER_PIN, GPIO_FUNC_PWM);
    slice = pwm_gpio_to_slice_num(BUZZER_PIN);
    chan = pwm_gpio_to_channel(BUZZER_PIN);
    pwm_set_chan_level(slice, chan, 0);
    pwm_set_enabled(slice, true);
}

void buzzer_off(void) {
    pwm_set_chan_level(slice, chan, 0);
}

static int64_t stop_cb(alarm_id_t id, void *user) {
    buzzer_off();
    stop_alarm = 0;
    return 0;
}

void buzzer_tone(uint32_t freq_hz, uint32_t duration_ms, uint32_t duty_pct) {
    // Pick a clock divider so wrap fits in 16 bits: sys_clk / div / (wrap+1) = freq.
    uint32_t sys = clock_get_hz(clk_sys);
    float div = (float)sys / (freq_hz * 65536.f);
    if (div < 1.f) div = 1.f;
    uint32_t wrap = (uint32_t)(sys / div / freq_hz) - 1;
    pwm_set_clkdiv(slice, div);
    pwm_set_wrap(slice, wrap);
    // Narrower pulses deliver less energy to the speaker: 50% is loudest.
    if (duty_pct > 50) duty_pct = 50;
    pwm_set_chan_level(slice, chan, (wrap + 1) * duty_pct / 100);

    if (stop_alarm) cancel_alarm(stop_alarm);
    stop_alarm = add_alarm_in_ms(duration_ms, stop_cb, NULL, true);
}

void buzzer_tick(void) {
    // A short, high burst reads as a mechanical "tick" rather than a beep.
    buzzer_tone(4000, 6, 6);
}
