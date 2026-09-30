// Firmware identity, reported by the VER serial command as
// "VERSION <FW_BOARD> <FW_VERSION> <build date>". The Windows app shows it and
// compares FW_VERSION with the firmware it bundles to offer updates.
#pragma once

#ifdef TD_BOARD_RP2350_128
#define FW_BOARD   "rp2350-128"   // Waveshare RP2350-Touch-LCD-1.28 (round)
#else
#define FW_BOARD   "rp2040-169"   // Waveshare RP2040-Touch-LCD-1.69
#endif
#define FW_VERSION "1.7.0"
