#pragma once
#include <stdbool.h>

typedef enum { EV_NONE, EV_TAP, EV_LONG, EV_SWIPE_L, EV_SWIPE_R, EV_SWIPE_U, EV_SWIPE_D } touch_ev_t;

typedef struct {
    touch_ev_t type;
    int x, y;          // where the touch started
} touch_event_t;

typedef struct {
    unsigned ints, reads, fails, recoveries, presses, events;
    int last_x, last_y, chip_id;
} touch_stats_t;
extern touch_stats_t touch_stats;

bool touch_init();
// Call every few ms: reads the chip only after it signals on TP_INT, and turns
// raw points into tap / long-press / swipe events.
touch_event_t touch_poll();
int touch_diag_lines();   // bit0 = SDA, bit1 = SCL levels
