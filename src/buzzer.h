#pragma once
#include <stdint.h>

void buzzer_init(void);
// Start a square-wave tone; it stops by itself after duration_ms.
// duty_pct sets loudness: 50 is the loudest, smaller is quieter.
void buzzer_tone(uint32_t freq_hz, uint32_t duration_ms, uint32_t duty_pct);
void buzzer_off(void);
// The once-per-second watch tick.
void buzzer_tick(void);
