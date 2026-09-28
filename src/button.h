// The BOOT button as a user input: the one button every Touch Deck board has.
#pragma once

typedef enum { BTN_NONE, BTN_SHORT, BTN_LONG } btn_ev_t;

void button_init(void);
// Call every ~20 ms from core0. Short press fires on release; long press
// fires once while still held (and then the release is swallowed).
btn_ev_t button_poll(void);
