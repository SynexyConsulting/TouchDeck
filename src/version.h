// Firmware identity, reported by the VER serial command as
// "VERSION <FW_BOARD> <FW_VERSION> <build date>". The Windows app shows it and
// compares FW_VERSION with the firmware it bundles to offer updates.
#pragma once

#define FW_BOARD   "rp2040-169"
#define FW_VERSION "1.6.0"
