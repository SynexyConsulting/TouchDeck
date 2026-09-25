// Composite USB device: CDC serial (PC helper link) + one HID interface
// carrying a keyboard (report ID 1) and a mouse (report ID 2).
#include <string.h>
#include "tusb.h"
#include "pico/unique_id.h"
#include "usb_io.h"

#define USB_VID 0xCAFE
#define USB_PID 0x4011   // tools/*.py look the board up by this VID:PID

static const tusb_desc_device_t desc_device = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    // IAD required for composite devices containing CDC
    .bDeviceClass = TUSB_CLASS_MISC,
    .bDeviceSubClass = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID,
    .idProduct = USB_PID,
    .bcdDevice = 0x0100,
    .iManufacturer = 1,
    .iProduct = 2,
    .iSerialNumber = 3,
    .bNumConfigurations = 1,
};

const uint8_t *tud_descriptor_device_cb(void) {
    return (const uint8_t *)&desc_device;
}

static const uint8_t desc_hid_report[] = {
    TUD_HID_REPORT_DESC_KEYBOARD(HID_REPORT_ID(REPORT_ID_KEYBOARD)),
    TUD_HID_REPORT_DESC_MOUSE(HID_REPORT_ID(REPORT_ID_MOUSE)),
};

const uint8_t *tud_hid_descriptor_report_cb(uint8_t instance) {
    (void)instance;
    return desc_hid_report;
}

enum { ITF_CDC_COMM, ITF_CDC_DATA, ITF_HID, ITF_TOTAL };

#define EP_CDC_NOTIF 0x81
#define EP_CDC_OUT   0x02
#define EP_CDC_IN    0x82
#define EP_HID_IN    0x83

#define CONFIG_LEN (TUD_CONFIG_DESC_LEN + TUD_CDC_DESC_LEN + TUD_HID_DESC_LEN)

static const uint8_t desc_config[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_TOTAL, 0, CONFIG_LEN, 0, 100),
    TUD_CDC_DESCRIPTOR(ITF_CDC_COMM, 4, EP_CDC_NOTIF, 8, EP_CDC_OUT, EP_CDC_IN, 64),
    TUD_HID_DESCRIPTOR(ITF_HID, 5, HID_ITF_PROTOCOL_NONE, sizeof(desc_hid_report), EP_HID_IN,
                       CFG_TUD_HID_EP_BUFSIZE, 5),
};

const uint8_t *tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return desc_config;
}

static const char *const strings[] = {
    NULL,                 // 0: language (handled below)
    "Waveshare DIY",      // 1: manufacturer
    "RP2040 Touch Deck",  // 2: product
    NULL,                 // 3: serial (chip unique ID)
    "Touch Deck Serial",  // 4: CDC
    "Touch Deck HID",     // 5: HID
};

static uint16_t desc_str[33];

const uint16_t *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    char serial[2 * PICO_UNIQUE_BOARD_ID_SIZE_BYTES + 1];
    const char *s;
    int n;

    if (index == 0) {
        desc_str[1] = 0x0409;
        n = 1;
    } else {
        if (index >= sizeof(strings) / sizeof(strings[0])) return NULL;
        if (index == 3) {
            pico_get_unique_board_id_string(serial, sizeof serial);
            s = serial;
        } else {
            s = strings[index];
        }
        n = (int)strlen(s);
        if (n > 32) n = 32;
        for (int i = 0; i < n; i++) desc_str[1 + i] = s[i];
    }
    desc_str[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2 * n + 2));
    return desc_str;
}
