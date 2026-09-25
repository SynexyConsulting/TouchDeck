// USB serial link to tools/clip_helper.py. Same line protocol as the RP2040
// firmware (see src/usb_io.c in the parent project), minus BOOT: PlatformIO's
// uploader resets the ESP32-C3 into its bootloader by itself.
#pragma once

void link_init();
void link_poll();                   // call often
void link_send_line(const char *line);
