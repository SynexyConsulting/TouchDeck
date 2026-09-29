// USB serial link to tools/clip_helper.py. Same line protocol as the RP2040
// firmware (see src/usb_io.c in the parent project), minus BOOT: PlatformIO's
// uploader resets the ESP32-C3 into its bootloader by itself.
#pragma once

void link_init();
void link_poll();                   // call often
bool link_send_line(const char *line);   // false: not sent (no room, or no app)
void link_state_poll();   // STATE lines for the app after WATCH 1 (logic loop)
