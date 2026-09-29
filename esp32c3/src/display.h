#pragma once
#include <stdint.h>

bool display_init();               // allocates fb, starts the GC9A01
void display_push();               // send fb to the panel (DMA)
void display_wait();               // block until the push is done
void display_push_rect(int x, int y, int w, int h);   // send part of fb (blocking)
void display_backlight(uint8_t level);
