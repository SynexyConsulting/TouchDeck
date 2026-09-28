// The BOOT button as a user input: the one button every Touch Deck board has.
#pragma once

enum btn_ev_t { BTN_NONE, BTN_SHORT, BTN_LONG };

void button_init();
// Call every ~20 ms. Short press fires on release; long press fires once while
// still held (and then the release is swallowed).
btn_ev_t button_poll();
