#pragma once
#include <stdbool.h>
#include <stdint.h>

#define REPORT_ID_KEYBOARD 1
#define REPORT_ID_MOUSE    2

void usb_io_init(void);
void usb_io_poll(void);           // call often: runs TinyUSB and the CDC protocol
bool usb_hid_ready(void);
bool usb_send_line(const char *line);   // false: not (completely) sent

bool usb_key(uint8_t modifier, uint8_t keycode);   // keycode 0 = release all
bool usb_mouse(uint8_t buttons, int8_t dx, int8_t dy);
bool usb_caps_lock(void);
void usb_state_poll(void);   // STATE lines for the app after WATCH 1 (main loop)
