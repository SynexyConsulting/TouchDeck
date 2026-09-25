#include <NimBLEDevice.h>
#include <NimBLEHIDDevice.h>
#include <Preferences.h>
#include "esp_random.h"
#include "nimble/nimble/host/include/host/ble_gatt.h"
#include "ble_hid.h"
#include "app.h"

#define REPORT_ID_KEYBOARD 1
#define REPORT_ID_MOUSE    2
#define PAIR_WINDOW_MS     120000

// Same layout as the RP2040's TinyUSB descriptors: keyboard with LED output
// report (ID 1) + 3-button mouse with wheel (ID 2).
static const uint8_t report_map[] = {
    0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, REPORT_ID_KEYBOARD,
    0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
    0x95, 0x01, 0x75, 0x08, 0x81, 0x01,
    0x95, 0x05, 0x75, 0x01, 0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02,
    0x95, 0x01, 0x75, 0x03, 0x91, 0x01,
    0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x65, 0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00,
    0xC0,
    0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, REPORT_ID_MOUSE, 0x09, 0x01, 0xA1, 0x00,
    0x05, 0x09, 0x19, 0x01, 0x29, 0x03, 0x15, 0x00, 0x25, 0x01, 0x95, 0x03, 0x75, 0x01, 0x81, 0x02,
    0x95, 0x01, 0x75, 0x05, 0x81, 0x03,
    0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x03, 0x81, 0x06,
    0xC0, 0xC0,
};

static NimBLEServer *server;
static NimBLECharacteristic *kbd_in, *kbd_out, *mouse_in;
static Preferences prefs;

static volatile uint8_t leds;
static volatile bool connected;
static volatile uint16_t conn_handle;
static volatile bool pairing, user_off;
static volatile bool want_name;          // read the PC's name after pairing
static uint32_t passkey, pair_until_ms;
static char host[33];

static bool bonded() { return NimBLEDevice::getNumBonds() > 0; }

static void update_advertising() {
    bool adv = pairing || (bonded() && !user_off && !connected);
    NimBLEAdvertising *a = NimBLEDevice::getAdvertising();
    if (adv && !a->isAdvertising()) a->start();
    else if (!adv && a->isAdvertising()) a->stop();
    app_redraw();
}

class ServerCB : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer *s, ble_gap_conn_desc *desc) override {
        connected = true;
        conn_handle = desc->conn_handle;
        app_redraw();
    }
    void onDisconnect(NimBLEServer *s) override {
        connected = false;
        update_advertising();
    }
    // DisplayOnly: this is the code the user types on the PC. Outside pairing
    // mode, hand out a random code nobody can see, so pairing just fails.
    uint32_t onPassKeyRequest() override {
        return pairing ? passkey : esp_random() % 1000000;
    }
    void onAuthenticationComplete(ble_gap_conn_desc *desc) override {
        if (!desc->sec_state.encrypted) {
            server->disconnect(desc->conn_handle);
            return;
        }
        if (pairing && desc->sec_state.bonded) {
            pairing = false;
            want_name = true;
            app_message("Paired");
        }
        app_redraw();
    }
};

// The host's only message to a keyboard: lock-key LED state.
class LedCB : public NimBLECharacteristicCallbacks {
    void onWrite(NimBLECharacteristic *c) override {
        std::string v = c->getValue();
        if (!v.empty()) leds = (uint8_t)v[0];
    }
};

// GAP Device Name (0x2A00) read from the PC's own GATT server = its computer name.
static int on_name(uint16_t conn, const struct ble_gatt_error *err, struct ble_gatt_attr *attr, void *) {
    if (err->status == 0 && attr) {
        uint16_t n = OS_MBUF_PKTLEN(attr->om);
        if (n > sizeof host - 1) n = sizeof host - 1;
        os_mbuf_copydata(attr->om, 0, n, host);
        host[n] = 0;
        prefs.putString("host", host);
        app_redraw();
    }
    return 0;
}

void ble_init() {
    prefs.begin("touchdeck", false);
    prefs.getString("host", host, sizeof host);

    NimBLEDevice::init("Touch Deck");
    NimBLEDevice::setPower(ESP_PWR_LVL_P9);
    NimBLEDevice::setSecurityAuth(true, true, true);   // bonding, MITM (passkey), secure connections
    NimBLEDevice::setSecurityIOCap(BLE_HS_IO_DISPLAY_ONLY);

    server = NimBLEDevice::createServer();
    server->setCallbacks(new ServerCB());
    server->advertiseOnDisconnect(false);   // update_advertising() decides

    NimBLEHIDDevice *hid = new NimBLEHIDDevice(server);
    kbd_in = hid->inputReport(REPORT_ID_KEYBOARD);
    kbd_out = hid->outputReport(REPORT_ID_KEYBOARD);
    mouse_in = hid->inputReport(REPORT_ID_MOUSE);
    kbd_out->setCallbacks(new LedCB());

    hid->manufacturer()->setValue("Waveshare DIY");
    hid->pnp(0x02, 0xE502, 0xA111, 0x0210);
    hid->hidInfo(0x00, 0x01);
    hid->reportMap((uint8_t *)report_map, sizeof report_map);
    hid->startServices();
    hid->setBatteryLevel(100);

    NimBLEAdvertising *adv = server->getAdvertising();
    adv->setAppearance(HID_KEYBOARD);
    adv->addServiceUUID(hid->hidService()->getUUID());
    adv->setScanResponse(true);
    update_advertising();
}

void ble_poll() {
    if (pairing && (int32_t)(now_ms() - pair_until_ms) >= 0) {
        pairing = false;
        app_message("Pairing timed out");
        update_advertising();
    }
    if (want_name && connected) {
        want_name = false;
        static const ble_uuid16_t name_uuid = BLE_UUID16_INIT(0x2A00);
        ble_gattc_read_by_uuid(conn_handle, 1, 0xFFFF, &name_uuid.u, on_name, nullptr);
    }
}

bt_state_t ble_state() {
    if (pairing) return BT_PAIRING;
    if (!bonded()) return BT_UNPAIRED;
    if (connected) return BT_CONNECTED;
    return user_off ? BT_OFF : BT_WAITING;
}

bool ble_connected() { return connected; }
bool ble_ready() { return connected && kbd_in->getSubscribedCount() > 0; }
uint32_t ble_passkey() { return passkey; }
int ble_pair_secs_left() { return pairing ? (int)((pair_until_ms - now_ms()) / 1000) : 0; }
const char *ble_host_name() { return host; }
bool ble_caps_lock() { return leds & 0x02; }

void ble_pair_start() {
    // One PC at a time: pairing a new one replaces the old bond.
    if (connected) server->disconnect(conn_handle);
    NimBLEDevice::deleteAllBonds();
    host[0] = 0;
    prefs.remove("host");
    passkey = esp_random() % 1000000;
    pair_until_ms = now_ms() + PAIR_WINDOW_MS;
    pairing = true;
    user_off = false;
    update_advertising();
}

void ble_pair_cancel() {
    pairing = false;
    if (connected && !bonded()) server->disconnect(conn_handle);
    update_advertising();
}

void ble_disconnect() {
    user_off = true;
    if (connected) server->disconnect(conn_handle);
    update_advertising();
}

void ble_reconnect() {
    user_off = false;
    update_advertising();
}

void ble_forget() {
    if (connected) server->disconnect(conn_handle);
    NimBLEDevice::deleteAllBonds();
    host[0] = 0;
    prefs.remove("host");
    user_off = false;
    update_advertising();
    app_message("Also remove it in Windows");
}

bool ble_key(uint8_t modifier, uint8_t keycode) {
    if (!ble_ready()) return false;
    uint8_t r[8] = {keycode ? modifier : (uint8_t)0, 0, keycode, 0, 0, 0, 0, 0};
    kbd_in->setValue(r, sizeof r);
    kbd_in->notify();
    return true;
}

bool ble_mouse(uint8_t buttons, int8_t dx, int8_t dy) {
    if (!ble_ready()) return false;
    uint8_t r[4] = {buttons, (uint8_t)dx, (uint8_t)dy, 0};
    mouse_in->setValue(r, sizeof r);
    mouse_in->notify();
    return true;
}
