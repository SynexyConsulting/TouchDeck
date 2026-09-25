#pragma once
#include <stdint.h>

void lcd_init(void);
void lcd_set_backlight(uint8_t percent);
// Push a full LCD_W x LCD_H RGB565 frame via DMA. Returns immediately;
// call lcd_wait() before touching the buffer again.
void lcd_push_frame(const uint16_t *fb);
void lcd_wait(void);
