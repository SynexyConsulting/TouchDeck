// Line icons drawn from anti-aliased strokes on a 24-unit grid centred on
// (cx, cy). Shared by the ESP32-C3 and RP2040 UIs.
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef void (*icon_fn)(float cx, float cy, float size, uint16_t col);
void icon_bt(float cx, float cy, float size, uint16_t col);
void icon_monitor(float cx, float cy, float size, uint16_t col);
void icon_copy(float cx, float cy, float size, uint16_t col);
void icon_arrow_right(float cx, float cy, float size, uint16_t col);
void icon_lock(float cx, float cy, float size, uint16_t col);
void icon_chevron_left(float cx, float cy, float size, uint16_t col);
void icon_chevron_right(float cx, float cy, float size, uint16_t col);
void icon_cog(float cx, float cy, float size, uint16_t col);   // hole is background-black
void icon_close(float cx, float cy, float size, uint16_t col);  // an X
void icon_trash(float cx, float cy, float size, uint16_t col);
#ifdef __cplusplus
}
#endif
