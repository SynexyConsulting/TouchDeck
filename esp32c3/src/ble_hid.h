// Bluetooth LE keyboard + mouse. The ESP32-C3 has no USB device controller
// for HID, so this replaces the RP2040's USB HID.
//
// Pairing is explicit, driven from the Settings > Bluetooth screen: the board
// only advertises to strangers while pairing mode is on, and pairing needs the
// 6-digit passkey shown on screen (DisplayOnly + MITM), so nobody can pair
// silently. A bonded PC can always reconnect unless the user tapped Disconnect.
#pragma once
#include <stdint.h>

#define KEY_MOD_LSHIFT 0x02
#define MOUSE_BTN_RIGHT 0x02

enum bt_state_t {
    BT_UNPAIRED,    // no bond; not advertising
    BT_PAIRING,     // advertising openly, passkey on screen
    BT_WAITING,     // bonded, advertising for that PC to reconnect
    BT_CONNECTED,   // bonded PC connected
    BT_OFF,         // bonded, but the user disconnected; not advertising
};

void ble_init();
void ble_poll();          // call from the logic loop: timeouts, host-name read

bt_state_t ble_state();
bool ble_connected();     // a host is connected
bool ble_bonded();        // a bond exists
bool ble_ready();         // connected and subscribed to our reports
uint32_t ble_passkey();   // valid in BT_PAIRING
int ble_pair_secs_left();
const char *ble_host_name();   // name of the bonded PC ("" if unknown)

void ble_pair_start();
void ble_pair_cancel();
void ble_disconnect();    // drop the link and stop advertising (bond kept)
void ble_reconnect();     // resume advertising for the bonded PC
void ble_forget();        // delete the bond

bool ble_key(uint8_t modifier, uint8_t keycode);   // keycode 0 = release all
bool ble_mouse(uint8_t buttons, int8_t dx, int8_t dy);
bool ble_caps_lock();
